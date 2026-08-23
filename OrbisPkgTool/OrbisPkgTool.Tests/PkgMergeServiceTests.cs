namespace OrbisPkgTool.Tests;

public sealed class PkgMergeServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opt_merge_" + Guid.NewGuid().ToString("N"));

    public PkgMergeServiceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }

    [Fact]
    public void MismatchedTitleIds_AreRejected()
    {
        var baseInfo = new PkgInfo { Type = PkgType.Game, TitleId = "CUSA00001" };
        var patchInfo = new PkgInfo { Type = PkgType.Patch, TitleId = "CUSA00002" };
        var ex = Assert.Throws<InvalidOperationException>(() => PkgMergeService.ValidatePackages("base.pkg", "patch.pkg", baseInfo, patchInfo));
        Assert.Contains("TITLE_ID mismatch", ex.Message);
    }

    [Fact]
    public void PatchCannotBeUsedAsBase()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PkgMergeService.ValidatePackages("base.pkg", "patch.pkg",
            new PkgInfo { Type = PkgType.Patch, TitleId = "CUSA00001" }, new PkgInfo { Type = PkgType.Patch, TitleId = "CUSA00001" }));
        Assert.Contains("base Game", ex.Message);
    }

    [Fact]
    public void NonPatchCannotBeUsedAsUpdate()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PkgMergeService.ValidatePackages("base.pkg", "update.pkg",
            new PkgInfo { Type = PkgType.Game, TitleId = "CUSA00001" }, new PkgInfo { Type = PkgType.Game, TitleId = "CUSA00001" }));
        Assert.Contains("Patch", ex.Message);
    }

    [Fact]
    public void OutputCannotEqualEitherInput()
    {
        string basePkg = Touch("base.pkg"), updatePkg = Touch("update.pkg");
        var request = new PkgMergeRequest { BasePkgPath = basePkg, UpdatePkgPath = updatePkg, OutputPkgPath = basePkg };
        var ex = Assert.Throws<ArgumentException>(() => PkgMergeService.ValidateRequest(request));
        Assert.Contains("must not be either input", ex.Message);
    }

    [Fact]
    public void SuppliedWorkDirectory_IsOnlyAParentForAnOwnedChild()
    {
        string parent = Path.Combine(_directory, "user-selected-work");
        string workOne = PkgMergeService.CreateWorkDirectory("C:\\games\\base.pkg", parent);
        string workTwo = PkgMergeService.CreateWorkDirectory("C:\\games\\base.pkg", parent);

        Assert.Equal(Path.GetFullPath(parent), Path.GetDirectoryName(workOne));
        Assert.NotEqual(workOne, workTwo);
        Assert.StartsWith("pkg_merge_base_", Path.GetFileName(workOne), StringComparison.Ordinal);
    }

    [Fact]
    public void MergeExtraction_ForwardsRequestCancellationToken()
    {
        string service = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OrbisPkgTool", "PkgMergeService.cs"));
        Assert.Contains("new ExtractAllOptions { CancellationToken = request.CancellationToken }", service);
    }

    [Fact]
    public void CliMerge_DelegatesToPublicService()
    {
        string program = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OrbisPkgTool", "Program.cs"));
        Assert.Contains("new OrbisPkgTool.PkgMergeService().Merge", program);
        Assert.DoesNotContain("RunMergeLegacy", program);
    }

    private string Touch(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, []);
        return path;
    }
}
