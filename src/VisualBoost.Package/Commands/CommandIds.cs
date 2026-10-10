namespace VisualBoost.Commands;

internal static class CommandIds
{
    public const int SwitchHeaderSource = 0x0100;
    public const int OpenOptions = 0x0101;
    public const int ShowIndexStatus = 0x0102;
    public const int OpenFileSearch = 0x0103;
    public const int OpenSymbolSearch = 0x0104;
    public const int OpenDocumentMembers = 0x0107;
    public const int GenerateFunction = 0x0108;
    public const int GoToDefinition = 0x0109;
    public const int FindReferences = 0x010A;
    public const int ShowReferencesWindow = 0x010B;
    public const int RebuildIndex = 0x010C;

    /// <summary>편집기 문맥 메뉴의 하위 메뉴(VSCT `VisualBoostCodeMenu`)입니다. 표시 여부만 처리하며 값은 VSCT와 같아야 합니다.</summary>
    public const int CodeContextMenu = 0x1024;
}
