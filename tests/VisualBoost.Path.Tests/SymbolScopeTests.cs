using System;
using System.Linq;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Indexing;

internal static class SymbolScopeTests
{
    internal static void Run()
    {
        var engine = @"C:\Fixture\Engine\Source\A.h";
        var game = @"C:\Fixture\Game\Main.h";
        var linked = @"D:\Shared\Linked.h";
        var unknown = @"C:\Fixture\Game\New.h";
        var first = SymbolSearchScope.Project("game.vcxproj", "Game", new[] { game, linked });
        var second = SymbolSearchScope.Project("other.vcxproj", "Other", new[] { linked });
        var catalog = SymbolSearchScope.CreateCatalog(new[] { first, second }, new[] { game, linked, engine }, new[] { @"C:\Fixture\Engine" });
        Check(first.Includes(linked) && second.Includes(linked), "연결 파일과 여러 프로젝트 소속 보존");
        Check(!first.Includes(unknown), "폴더가 같아도 미등록 파일을 프로젝트 소속으로 추측하지 않음");
        Check(catalog.Single(s => s.Id == "engine").Includes(engine) && !catalog.Single(s => s.Id == "engine").Includes(@"C:\Fixture\EngineBackup\A.h"), "엔진 경로 경계 보존");
        Check(catalog.Single(s => s.Id == "registered").Includes(linked) && !catalog.Single(s => s.Id == "registered").Includes(engine), "등록 프로젝트와 확인된 엔진 분리");
        Check(catalog.Single(s => s.Id == "unassigned").Includes(unknown), "미등록 파일은 소속 미확인으로 유지");
        var excluded = Enumerable.Range(0, 400).Select(i => new SourceSymbolLocation("Main", engine, i + 1, 1, SourceSymbolKind.Function)).ToArray();
        var wanted = new SourceSymbolLocation("Main", game, 1, 1, SourceSymbolKind.Function);
        using var index = new SourceSymbolIndex();
        index.ReplaceAll(excluded);
        index.AppendBatch(new[] { wanted });
        Check(index.Search("Main", 2, default, first.Includes).Single().Location == wanted, "상위 결과 제한 전에 범위를 적용하고 부분 인덱스까지 검색");
        Check(index.Count == 401 && index.Find("Main").Count == 401, "탐색 필터가 이름 원본 인덱스를 제거하지 않음");
        index.ReplaceAll(excluded.Concat(new[] { wanted }));
        Check(index.Search("Main", 2, default, first.Includes).Single().Location == wanted, "최종 압축 후에도 범위 결과 유지");
        using var token = new CancellationTokenSource(); token.Cancel();
        try { index.Search("Main", 2, token.Token, first.Includes); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
