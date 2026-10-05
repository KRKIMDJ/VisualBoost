using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>compilation database 한 항목입니다. 인자의 첫 값은 컴파일러입니다.</summary>
public sealed class CompileCommand
{
    public CompileCommand(string directory, string file, IReadOnlyList<string> arguments)
    {
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        File = file ?? throw new ArgumentNullException(nameof(file));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    public string Directory { get; }

    public string File { get; }

    public IReadOnlyList<string> Arguments { get; }

    public JsonValue ToJson() => JsonValue.Object(
        ("directory", Directory),
        ("file", File),
        ("arguments", JsonValue.Array(Arguments.Select(a => (JsonValue)a))));

    public static CompileCommand? FromJson(JsonValue entry)
    {
        var directory = entry["directory"].AsString();
        var file = entry["file"].AsString();
        var arguments = entry["arguments"].Items.Select(a => a.AsString()).Where(a => a is not null).Select(a => a!).ToArray();
        if (arguments.Length == 0 && entry["command"].AsString() is string command)
        {
            arguments = CommandLine.Split(command).ToArray();
        }

        if (directory is null || file is null || arguments.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathRooted(file))
        {
            file = Path.GetFullPath(Path.Combine(directory, file));
        }

        return new CompileCommand(directory, file, arguments);
    }
}

public static class CompileCommandDatabase
{
    public const string FileName = "compile_commands.json";

    /// <summary>임시 파일에 쓴 뒤 교체해 clangd가 반쯤 쓴 파일을 읽지 않게 합니다.</summary>
    public static void Write(string directory, IEnumerable<CompileCommand> commands) => WriteText(directory, Serialize(commands));

    /// <summary>
    /// 내용이 같으면 파일을 건드리지 않습니다. clangd가 수정 시각을 보고 데이터베이스를 다시 읽으므로
    /// 불필요한 재해석과 재색인 판정을 피합니다. 바뀌어 썼으면 true입니다.
    /// </summary>
    public static bool WriteIfChanged(string directory, IEnumerable<CompileCommand> commands)
    {
        var text = Serialize(commands);
        var target = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(target) && string.Equals(File.ReadAllText(target), text, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (IOException)
        {
            // 읽을 수 없으면 새로 씁니다.
        }

        WriteText(directory, text);
        return true;
    }

    public static string Serialize(IEnumerable<CompileCommand> commands)
    {
        var builder = new StringBuilder("[\n");
        var first = true;
        foreach (var command in commands)
        {
            if (!first) builder.Append(",\n");
            first = false;
            builder.Append(command.ToJson().ToJson());
        }

        builder.Append("\n]\n");
        return builder.ToString();
    }

    private static void WriteText(string directory, string text)
    {
        System.IO.Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, FileName);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        if (File.Exists(target))
        {
            File.Replace(temporary, target, null);
        }
        else
        {
            File.Move(temporary, target);
        }
    }

    public static IReadOnlyList<CompileCommand> Read(string path) =>
        JsonValue.Parse(File.ReadAllText(path)).Items.Select(CompileCommand.FromJson).Where(c => c is not null).Select(c => c!).ToArray();
}

/// <summary>Windows 명령줄·응답 파일 인자 분리 규칙(따옴표와 역슬래시)입니다.</summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hasToken = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                var j = i;
                while (j < text.Length && text[j] == '\\') j++;
                var count = j - i;
                if (j < text.Length && text[j] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        i = j + 1;
                    }
                    else
                    {
                        i = j;
                    }
                }
                else
                {
                    current.Append('\\', count);
                    i = j;
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if ((c == ' ' || c == '\t' || c == '\r' || c == '\n') && !quoted)
            {
                if (hasToken) args.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }

            i++;
        }

        if (hasToken) args.Add(current.ToString());
        return args;
    }
}
