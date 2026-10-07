using System;

namespace VisualBoost.Core.Input;

/// <summary>
/// VS 자동화(<c>Command.Bindings</c>)가 돌려주는 키 바인딩 문자열(<c>범위::키</c>)을 해석합니다. 범위 이름은 VS 표시 언어로 바뀌므로 보지 않고,
/// 키 부분(<c>Alt+G</c>, <c>Ctrl+K, Ctrl+C</c> 등)만 봅니다.
/// </summary>
public static class KeyBindingText
{
    /// <summary>바인딩이 Alt와 글자 하나로 된 단일 키(<c>Alt+G</c>)인지 봅니다. 다른 수식 키가 섞이거나 두 단계 키이면 false입니다.</summary>
    public static bool IsAltLetter(string? binding, char letter)
    {
        if (binding is null) return false;
        var separator = binding.LastIndexOf("::", StringComparison.Ordinal);
        var keys = (separator < 0 ? binding : binding.Substring(separator + 2)).Trim();
        return keys.Length == 5 && keys.StartsWith("Alt+", StringComparison.OrdinalIgnoreCase) &&
               char.ToUpperInvariant(keys[4]) == char.ToUpperInvariant(letter);
    }
}
