using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>서버가 JSON-RPC 오류 응답을 보냈습니다.</summary>
public sealed class LspRequestException : Exception
{
    public LspRequestException(string method, int code, string message)
        : base($"{method}: {message} ({code})")
    {
        Method = method;
        Code = code;
    }

    public string Method { get; }

    public int Code { get; }
}

/// <summary>연결이 끊겨 요청을 완료할 수 없습니다. 이 연결은 다시 쓸 수 없습니다.</summary>
public sealed class LspConnectionClosedException : Exception
{
    public LspConnectionClosedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Content-Length 머리글로 구분한 JSON-RPC 2.0 전송 계층입니다(LSP base protocol).
/// </summary>
/// <remarks>
/// 읽기는 전용 스레드 하나가 맡고, 쓰기는 잠금으로 직렬화합니다. 응답·통지 처리기는 읽기 스레드에서
/// 호출되므로 처리기는 짧게 끝나야 하며 UI thread로 직접 넘어가지 않습니다. 요청 완료는
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>로 읽기 스레드 밖에서 이어집니다.
/// 스트림이 끝나거나 손상된 메시지를 받으면 연결 전체를 실패로 두고 모든 대기 요청을 같은 오류로 끝냅니다.
/// </remarks>
public sealed class LspConnection : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly Stream input;
    private readonly Stream output;
    private readonly int maxMessageBytes;
    private readonly object writeLock = new();
    private readonly ConcurrentDictionary<int, Pending> pending = new();
    private Thread? reader;
    private int nextId;
    private int disposed;
    private Exception? failure;

    public LspConnection(Stream input, Stream output, int maxMessageBytes = 64 * 1024 * 1024)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.maxMessageBytes = maxMessageBytes;
    }

    /// <summary>서버 통지(method, params). 읽기 스레드에서 호출됩니다.</summary>
    public event Action<string, JsonValue>? Notification;

    /// <summary>연결이 실패하거나 닫혔을 때 한 번 호출됩니다.</summary>
    public event Action<Exception>? Closed;

    /// <summary>
    /// 서버가 보낸 요청(method, params)의 결과를 만듭니다. null을 돌려주면 MethodNotFound로 응답합니다.
    /// 응답 결과 자체가 JSON null이어야 하면 <see cref="JsonValue.Null"/>을 돌려줍니다.
    /// </summary>
    public Func<string, JsonValue, JsonValue?>? ServerRequestHandler { get; set; }

    public Exception? Failure => Volatile.Read(ref failure);

    public int PendingCount => pending.Count;

    public void Start()
    {
        if (reader is not null)
        {
            throw new InvalidOperationException("이미 시작한 연결입니다.");
        }

        reader = new Thread(ReadLoop) { IsBackground = true, Name = "VisualBoost LSP reader" };
        reader.Start();
    }

    public Task<JsonValue> RequestAsync(string method, JsonValue? parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailed();
        var id = Interlocked.Increment(ref nextId);
        var entry = new Pending(method);
        pending[id] = entry;
        try
        {
            Send(JsonValue.Object(("jsonrpc", "2.0"), ("id", id), ("method", method), ("params", parameters ?? JsonValue.Null)));
        }
        catch
        {
            pending.TryRemove(id, out _);
            throw;
        }

        if (cancellationToken.CanBeCanceled)
        {
            // 취소 통지는 서버 계산의 중단을 보장하지 않습니다. 호출자는 즉시 취소로 끝내고 늦은 응답은 버립니다.
            entry.Registration = cancellationToken.Register(() =>
            {
                if (pending.TryRemove(id, out var cancelled))
                {
                    cancelled.Completion.TrySetCanceled(cancellationToken);
                    TrySend(JsonValue.Object(("jsonrpc", "2.0"), ("method", "$/cancelRequest"), ("params", JsonValue.Object(("id", id)))));
                }
            });
            if (entry.Completion.Task.IsCompleted)
            {
                // 등록 전에 응답이 이미 도착한 경우 등록을 남겨 두지 않습니다.
                entry.Registration.Dispose();
            }
        }

        return entry.Completion.Task;
    }

    public void Notify(string method, JsonValue? parameters)
    {
        ThrowIfFailed();
        Send(JsonValue.Object(("jsonrpc", "2.0"), ("method", method), ("params", parameters ?? JsonValue.Null)));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Fail(new LspConnectionClosedException("LSP 연결을 닫았습니다."));
        try
        {
            output.Dispose();
        }
        catch (IOException)
        {
            // 상대 프로세스가 이미 끝난 경우입니다.
        }

        try
        {
            input.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private void ThrowIfFailed()
    {
        var current = Volatile.Read(ref failure);
        if (current is not null)
        {
            throw new LspConnectionClosedException("LSP 연결이 끊겼습니다.", current);
        }
    }

    private void Send(JsonValue message)
    {
        var body = Utf8.GetBytes(message.ToJson());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        lock (writeLock)
        {
            ThrowIfFailed();
            try
            {
                output.Write(header, 0, header.Length);
                output.Write(body, 0, body.Length);
                output.Flush();
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
            {
                Fail(exception);
                throw new LspConnectionClosedException("LSP 메시지를 보내지 못했습니다.", exception);
            }
        }
    }

    private void TrySend(JsonValue message)
    {
        try
        {
            Send(message);
        }
        catch (LspConnectionClosedException)
        {
            // 취소·응답 전송 실패는 연결 실패로 이미 전파됩니다.
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var length = ReadHeader();
                if (length < 0)
                {
                    throw new EndOfStreamException("LSP 서버 출력이 끝났습니다.");
                }

                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var count = input.Read(body, read, length - read);
                    if (count <= 0)
                    {
                        throw new EndOfStreamException("LSP 메시지 본문이 잘렸습니다.");
                    }

                    read += count;
                }

                Dispatch(JsonValue.Parse(Utf8.GetString(body)));
            }
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is ObjectDisposedException ||
            exception is FormatException ||
            exception is DecoderFallbackException ||
            exception is InvalidDataException)
        {
            Fail(exception);
        }
    }

    /// <summary>머리글을 읽어 본문 길이를 돌려줍니다. 시작 전에 스트림이 끝나면 -1입니다.</summary>
    private int ReadHeader()
    {
        var length = -1;
        var line = new StringBuilder();
        var sawAny = false;
        while (true)
        {
            var b = input.ReadByte();
            if (b < 0)
            {
                if (!sawAny) return -1;
                throw new EndOfStreamException("LSP 머리글이 잘렸습니다.");
            }

            sawAny = true;
            if (b == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length < 0) throw new InvalidDataException("Content-Length 머리글이 없습니다.");
                    return length;
                }

                var colon = text.IndexOf(':');
                if (colon > 0 && string.Equals(text.Substring(0, colon).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(text.Substring(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                        length <= 0 || length > maxMessageBytes)
                    {
                        throw new InvalidDataException("허용하지 않는 LSP 메시지 크기입니다: " + text);
                    }
                }

                continue;
            }

            if (line.Length > 1024)
            {
                throw new InvalidDataException("LSP 머리글이 너무 깁니다.");
            }

            line.Append((char)b);
        }
    }

    private void Dispatch(JsonValue message)
    {
        var method = message["method"].AsString();
        var id = message["id"];
        if (method is null)
        {
            var key = id.AsInt32();
            if (key is int requestId && pending.TryRemove(requestId, out var entry))
            {
                entry.Registration.Dispose();
                var error = message["error"];
                if (!error.IsNull)
                {
                    entry.Completion.TrySetException(new LspRequestException(entry.Method, error["code"].AsInt32() ?? 0,
                        error["message"].AsString() ?? "알 수 없는 오류"));
                }
                else
                {
                    entry.Completion.TrySetResult(message["result"]);
                }
            }

            // 취소했거나 알 수 없는 ID의 늦은 응답은 버립니다.
            return;
        }

        var parameters = message["params"];
        if (id.IsNull)
        {
            Notification?.Invoke(method, parameters);
            return;
        }

        var result = ServerRequestHandler?.Invoke(method, parameters);
        if (result is null)
        {
            var error = JsonValue.Object(("code", -32601), ("message", "Unsupported client request: " + method));
            TrySend(JsonValue.Object(("jsonrpc", "2.0"), ("id", id), ("error", error)));
        }
        else
        {
            TrySend(JsonValue.Object(("jsonrpc", "2.0"), ("id", id), ("result", result)));
        }
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.CompareExchange(ref failure, exception, null) is not null)
        {
            return;
        }

        var closed = exception as LspConnectionClosedException ?? new LspConnectionClosedException("LSP 연결이 끊겼습니다.", exception);
        foreach (var key in pending.Keys)
        {
            if (pending.TryRemove(key, out var entry))
            {
                entry.Registration.Dispose();
                entry.Completion.TrySetException(closed);
            }
        }

        Closed?.Invoke(exception);
    }

    private sealed class Pending
    {
        public Pending(string method) => Method = method;

        public string Method { get; }

        public TaskCompletionSource<JsonValue> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}
