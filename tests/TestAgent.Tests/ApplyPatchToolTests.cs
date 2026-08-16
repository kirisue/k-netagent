using System.Security.Cryptography;
using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class ApplyPatchToolTests
{
    [Fact]
    public async Task Applies_changes_to_two_files_and_reports_both_modified_files()
    {
        var root = NewWorkspace("success");
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "alpha\nbeta");
            await File.WriteAllTextAsync(Path.Combine(root, "two.txt"), "gamma\ndelta");
            var request = Request(new
            {
                changes = new[]
                {
                    new { path = "one.txt", expectedSha256 = Sha256("alpha\nbeta"), replacements = new[] { new { oldText = "alpha", newText = "one" } } },
                    new { path = "two.txt", expectedSha256 = Sha256("gamma\ndelta"), replacements = new[] { new { oldText = "delta", newText = "two" } } }
                }
            });

            var result = await new ApplyPatchTool(new WorkspaceLocator()).ExecuteAsync(request);

            Assert.Equal(ToolExecutionStatus.Success, result.Status);
            Assert.Equal("one\nbeta", await File.ReadAllTextAsync(Path.Combine(root, "one.txt")));
            Assert.Equal("gamma\ntwo", await File.ReadAllTextAsync(Path.Combine(root, "two.txt")));
            Assert.Equal(["one.txt", "two.txt"], result.ModifiedFiles);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mismatch_in_second_file_leaves_all_files_unchanged()
    {
        var root = NewWorkspace("atomic-mismatch");
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            const string firstOriginal = "alpha\nbeta";
            const string secondOriginal = "gamma\ndelta";
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), firstOriginal);
            await File.WriteAllTextAsync(Path.Combine(root, "two.txt"), secondOriginal);
            var request = Request(new
            {
                changes = new[]
                {
                    new { path = "one.txt", expectedSha256 = Sha256(firstOriginal), replacements = new[] { new { oldText = "alpha", newText = "one" } } },
                    new { path = "two.txt", expectedSha256 = Sha256(secondOriginal), replacements = new[] { new { oldText = "missing", newText = "two" } } }
                }
            });

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApplyPatchTool(new WorkspaceLocator()).ExecuteAsync(request));

            Assert.Equal(firstOriginal, await File.ReadAllTextAsync(Path.Combine(root, "one.txt")));
            Assert.Equal(secondOriginal, await File.ReadAllTextAsync(Path.Combine(root, "two.txt")));
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Stale_expected_sha256_does_not_write_any_file()
    {
        var root = NewWorkspace("stale-sha");
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        try
        {
            const string original = "current content";
            var path = Path.Combine(root, "one.txt");
            await File.WriteAllTextAsync(path, original);
            var request = Request(new
            {
                changes = new[]
                {
                    new
                    {
                        path = "one.txt",
                        expectedSha256 = Sha256("older content"),
                        replacements = new[] { new { oldText = "current", newText = "changed" } }
                    }
                }
            });

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApplyPatchTool(new WorkspaceLocator()).ExecuteAsync(request));

            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    private static ToolRequest Request(object arguments) =>
        new("patch-request", "apply_patch", JsonSerializer.Serialize(arguments), "test-session");

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string NewWorkspace(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"TestAgent-apply-patch-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "TestAgent.slnx"), "<Solution />");
        return root;
    }
}
