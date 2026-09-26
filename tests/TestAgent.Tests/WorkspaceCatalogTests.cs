using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WorkspaceCatalogTests
{
    [Fact]
    public async Task Register_existing_workspace_persists_and_restores_on_startup()
    {
        using var storage = TestDirectory.Storage();
        using var workspace = TestDirectory.Workspace();
        var paths = new AppPaths(storage.Path);

        var registered = await new JsonWorkspaceCatalog(paths)
            .RegisterExistingAsync(Path.Combine(workspace.Path, "."));

        var restartedCatalog = new JsonWorkspaceCatalog(paths);
        var restored = await restartedCatalog.ResolveStartupAsync();
        var state = await restartedCatalog.GetStateAsync();

        Assert.Equal(registered.Id, restored.Id);
        Assert.Equal(Path.GetFullPath(workspace.Path), restored.Root,
            ignoreCase: true, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false);
        Assert.Equal(restored.Id, state.ActiveWorkspaceId);
        Assert.Equal(restored.Id, Assert.Single(state.Recent).Id);
    }

    [Fact]
    public async Task Registering_the_same_workspace_is_idempotent()
    {
        using var storage = TestDirectory.Storage();
        using var workspace = TestDirectory.Workspace();
        var catalog = new JsonWorkspaceCatalog(new AppPaths(storage.Path));

        var first = await catalog.RegisterExistingAsync(workspace.Path);
        var second = await catalog.RegisterExistingAsync(
            workspace.Path + Path.DirectorySeparatorChar);
        var state = await catalog.GetStateAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Root, second.Root, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(second.Id, state.ActiveWorkspaceId);
        Assert.Single(state.Recent);
    }

    [Fact]
    public async Task Create_makes_one_empty_child_and_persists_it_as_active()
    {
        using var storage = TestDirectory.Storage();
        using var parent = TestDirectory.Workspace();
        var catalog = new JsonWorkspaceCatalog(new AppPaths(storage.Path));

        var created = await catalog.CreateAsync(parent.Path, "Sample Project");
        var expected = Path.Combine(parent.Path, "Sample Project");
        var state = await new JsonWorkspaceCatalog(new AppPaths(storage.Path)).GetStateAsync();

        Assert.True(Directory.Exists(expected));
        Assert.Empty(Directory.EnumerateFileSystemEntries(expected));
        Assert.Equal(Path.GetFullPath(expected), created.Root,
            ignoreCase: true, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false);
        Assert.Equal(created.Id, state.ActiveWorkspaceId);
        Assert.Equal(created.Id, Assert.Single(state.Recent).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nested/project")]
    [InlineData("nested\\project")]
    [InlineData("CON")]
    [InlineData("LPT1.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public async Task Create_rejects_invalid_project_names_without_creating_or_persisting(string name)
    {
        using var storage = TestDirectory.Storage();
        using var parent = TestDirectory.Workspace();
        var catalog = new JsonWorkspaceCatalog(new AppPaths(storage.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.CreateAsync(parent.Path, name));

        Assert.Empty(Directory.EnumerateFileSystemEntries(parent.Path));
        Assert.Empty((await catalog.GetStateAsync()).Recent);
    }

    [Fact]
    public async Task Create_does_not_overwrite_an_existing_folder_or_change_catalog_state()
    {
        using var storage = TestDirectory.Storage();
        using var parent = TestDirectory.Workspace();
        var existing = Path.Combine(parent.Path, "Existing");
        Directory.CreateDirectory(existing);
        var sentinel = Path.Combine(existing, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var catalog = new JsonWorkspaceCatalog(new AppPaths(storage.Path));

        await Assert.ThrowsAsync<IOException>(() => catalog.CreateAsync(parent.Path, "Existing"));

        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
        Assert.Empty((await catalog.GetStateAsync()).Recent);
    }

    [Fact]
    public async Task Register_rejects_broad_or_sensitive_system_roots()
    {
        using var storage = TestDirectory.Storage();
        var catalog = new JsonWorkspaceCatalog(new AppPaths(storage.Path));
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.RegisterExistingAsync(driveRoot));
        if (!string.IsNullOrWhiteSpace(localAppData))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                catalog.RegisterExistingAsync(localAppData));

        Assert.Empty((await catalog.GetStateAsync()).Recent);
    }

    [Fact]
    public async Task Register_rejects_a_workspace_reached_through_a_reparse_point_when_supported()
    {
        using var storage = TestDirectory.Storage();
        using var parent = TestDirectory.Workspace();
        using var target = TestDirectory.Workspace();
        var link = Path.Combine(parent.Path, "linked-workspace");
        try
        {
            Directory.CreateSymbolicLink(link, target.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                           PlatformNotSupportedException)
        {
            // Windows without Developer Mode cannot create the fixture. The same policy is also
            // covered by the production path walk and the existing workspace-tool link test.
            return;
        }

        var catalog = new JsonWorkspaceCatalog(new AppPaths(storage.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.RegisterExistingAsync(link));
        Assert.Empty((await catalog.GetStateAsync()).Recent);
    }

    [Fact]
    public async Task Corrupt_catalog_is_quarantined_and_loaded_as_empty()
    {
        using var storage = TestDirectory.Storage();
        var paths = new AppPaths(storage.Path);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.Workspaces, "{ this is not valid json");

        var state = await new JsonWorkspaceCatalog(paths).GetStateAsync();

        Assert.Null(state.ActiveWorkspaceId);
        Assert.Empty(state.Recent);
        Assert.False(File.Exists(paths.Workspaces));
        Assert.Single(Directory.EnumerateFiles(paths.Root, "workspaces.json.corrupt.*"));
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string parent)
        {
            Path = System.IO.Path.Combine(parent, "KNetAgent.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public static TestDirectory Storage() => new(System.IO.Path.GetTempPath());

        public static TestDirectory Workspace()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents) || documents.StartsWith("\\\\", StringComparison.Ordinal))
                documents = System.IO.Path.GetDirectoryName(FindRepositoryRoot())
                    ?? FindRepositoryRoot();
            return new(documents);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "TestAgent.slnx")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate TestAgent.slnx for test workspace setup.");
        }
    }
}
