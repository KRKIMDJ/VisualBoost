using System;

namespace VisualBoost.Core.CodeGeneration;

public enum GenerationDirection { Definition, Declaration }

// 위치와 소속은 동기화된 공개 언어 모델에서 캡처하고, 원문과 다시 대조합니다.
public sealed class GenerationFunction
{
    public GenerationFunction(string name, string owner, int start, int nameStart, int end, bool isDefinition)
    { Name = name; Owner = owner; Start = start; NameStart = nameStart; End = end; IsDefinition = isDefinition; }
    public string Name { get; }
    public string Owner { get; }
    public int Start { get; }
    public int NameStart { get; }
    public int End { get; }
    public bool IsDefinition { get; }
}

public sealed class GenerationClass
{
    public GenerationClass(string fullName, int start, int nameStart, int end)
    { FullName = fullName; Start = start; NameStart = nameStart; End = end; }
    public string FullName { get; }
    public int Start { get; }
    public int NameStart { get; }
    public int End { get; }
}

public sealed class GenerationPlan
{
    public GenerationPlan(int offset, string text, string signature, string warning)
    { Offset = offset; Text = text; Signature = signature; Warning = warning; }
    public int Offset { get; }
    public string Text { get; }
    public string Signature { get; }
    public string Warning { get; }
}

public sealed class GenerationNotSupportedException : Exception
{
    public GenerationNotSupportedException(string message) : base(message) { }
}
