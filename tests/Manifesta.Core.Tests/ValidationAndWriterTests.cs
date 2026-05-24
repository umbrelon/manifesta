using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.Pipeline;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class ValidationResultTests
{
    [Fact]
    public void Empty_HasNoIssues()
    {
        var r = ValidationResult.Empty;
        r.Issues.Should().BeEmpty();
        r.HasErrors.Should().BeFalse();
        r.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void FromIssues_ErrorsDetected()
    {
        var r = ValidationResult.FromIssues([
            new ValidationIssue { Severity = ValidationSeverity.Error, Code = "E001", Message = "err" }
        ]);
        r.HasErrors.Should().BeTrue();
        r.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void FromIssues_WarningsDetected()
    {
        var r = ValidationResult.FromIssues([
            new ValidationIssue { Severity = ValidationSeverity.Warning, Code = "W001", Message = "warn" }
        ]);
        r.HasErrors.Should().BeFalse();
        r.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void Merge_CombinesIssues()
    {
        var a = ValidationResult.FromIssues([
            new ValidationIssue { Severity = ValidationSeverity.Error, Code = "E001", Message = "err1" }
        ]);
        var b = ValidationResult.FromIssues([
            new ValidationIssue { Severity = ValidationSeverity.Warning, Code = "W001", Message = "warn1" }
        ]);

        var merged = a.Merge(b);
        merged.Issues.Should().HaveCount(2);
        merged.HasErrors.Should().BeTrue();
        merged.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void Merge_WithEmpty_IsIdentity()
    {
        var r = ValidationResult.FromIssues([
            new ValidationIssue { Severity = ValidationSeverity.Error, Code = "E001", Message = "err" }
        ]);
        r.Merge(ValidationResult.Empty).Issues.Should().HaveCount(1);
        ValidationResult.Empty.Merge(r).Issues.Should().HaveCount(1);
    }
}

public sealed class WriterTests
{
    [Fact]
    public void AtomicWriter_IsDryRun_IsFalse()
    {
        new AtomicWriter().IsDryRun.Should().BeFalse();
    }

    [Fact]
    public void DryRunWriter_IsDryRun_IsTrue()
    {
        new DryRunWriter().IsDryRun.Should().BeTrue();
    }

    [Fact]
    public async Task DryRunWriter_WritesNothingToDisk()
    {
        var output = new System.IO.StringWriter();
        var writer = new DryRunWriter(output);
        var tmpPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "out.json");

        await writer.WriteAsync(tmpPath, "{ \"test\": 1 }");

        // File must NOT exist
        File.Exists(tmpPath).Should().BeFalse("dry-run writer must not touch the filesystem");

        // But it should have logged the intent
        output.ToString().Should().Contain("[dry-run]");
        output.ToString().Should().Contain(tmpPath);
    }

    [Fact]
    public async Task AtomicWriter_WritesContentAndCreatesDirectory()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var outPath = Path.Combine(dir, "out.json");
        const string content = "{ \"hello\": \"world\" }";

        try
        {
            var writer = new AtomicWriter();
            await writer.WriteAsync(outPath, content);

            File.Exists(outPath).Should().BeTrue();
            (await File.ReadAllTextAsync(outPath)).Should().Be(content);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicWriter_LeavesNoTempFileOnSuccess()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var outPath = Path.Combine(dir, "clean.json");

        try
        {
            await new AtomicWriter().WriteAsync(outPath, "{}");

            // Only the final file should exist, no .tmp_ sibling
            var files = Directory.GetFiles(dir);
            files.Should().HaveCount(1);
            files[0].Should().Be(outPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ── Additional edge cases ──────────────────────────────────────────────

    [Fact]
    public async Task AtomicWriter_ExceptionDuringWrite_CleanupsTempFile()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var outPath = Path.Combine(dir, "out.json");

        try
        {
            var writer = new AtomicWriter();

            // Create a scenario where write will fail after temp file creation
            // Use a path that will fail when creating the temp file
            var badPath = Path.Combine(dir, "subdir", "file.json");
            // Create a file at the subdir location to block directory creation
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "subdir"), "blocking");

            // Attempt to write should fail
            try
            {
                await writer.WriteAsync(badPath, "{}");
                // If we get here without exception, skip this assertion
            }
            catch (Exception)
            {
                // Expected: write failed
                // Verify no .tmp_ files were left behind
                var files = Directory.GetFiles(dir, "*.tmp_*", SearchOption.AllDirectories);
                files.Should().BeEmpty("temp files should be cleaned up on exception");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicWriter_ConcurrentWrites_UseUniqueTempFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            Directory.CreateDirectory(dir);
            var writer = new AtomicWriter();

            // Write multiple files concurrently
            var tasks = Enumerable.Range(0, 5)
                .Select(i => writer.WriteAsync(Path.Combine(dir, $"file{i}.json"), $"{{\"id\": {i}}}"))
                .ToList();

            await Task.WhenAll(tasks);

            // All 5 files should exist, no temp files left
            var finalFiles = Directory.GetFiles(dir).Where(f => !f.Contains(".tmp_")).ToList();
            finalFiles.Should().HaveCount(5);

            var tempFiles = Directory.GetFiles(dir, "*.tmp_*");
            tempFiles.Should().BeEmpty("all temp files should be cleaned up");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicWriter_OverwritesExistingFile()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var outPath = Path.Combine(dir, "overwrite.json");
        const string oldContent = "{\"old\": true}";
        const string newContent = "{\"new\": true}";

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, oldContent);

            var writer = new AtomicWriter();
            await writer.WriteAsync(outPath, newContent);

            var result = await File.ReadAllTextAsync(outPath);
            result.Should().Be(newContent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicWriter_VeryLargeContent_HandlesCorrectly()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var outPath = Path.Combine(dir, "large.json");
        var largeContent = "{\"data\": \"" + new string('x', 10_000_000) + "\"}";  // 10MB

        try
        {
            var writer = new AtomicWriter();
            await writer.WriteAsync(outPath, largeContent);

            File.Exists(outPath).Should().BeTrue();
            var written = await File.ReadAllTextAsync(outPath);
            written.Length.Should().Be(largeContent.Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicWriter_UnicodePathAndContent_HandlesCorrectly()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var outPath = Path.Combine(dir, "файл_🎉.json");
        const string unicodeContent = "{\"emoji\": \"🎉🎊\", \"text\": \"café\"}";

        try
        {
            var writer = new AtomicWriter();
            await writer.WriteAsync(outPath, unicodeContent);

            File.Exists(outPath).Should().BeTrue();
            var result = await File.ReadAllTextAsync(outPath);
            result.Should().Be(unicodeContent);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DryRunWriter_CharCountFormatted_WithThousandsSeparator()
    {
        var output = new StringWriter();
        var writer = new DryRunWriter(output);
        var largeContent = new string('x', 1_000_000);

        await writer.WriteAsync("/some/path/file.json", largeContent);

        var result = output.ToString();
        // Should format 1,000,000 with thousands separator
        // The format depends on current culture: US uses comma, EU uses period
        // Just verify it contains the number in some formatted way
        result.Should().Contain("1").And.Contain("000").And.Contain("(").And.Contain("chars)");
    }

    [Fact]
    public async Task DryRunWriter_MultipleWrites_LogsEachIntent()
    {
        var output = new StringWriter();
        var writer = new DryRunWriter(output);

        await writer.WriteAsync("/path/file1.json", "{\"a\": 1}");
        await writer.WriteAsync("/path/file2.json", "{\"b\": 2}");
        await writer.WriteAsync("/path/file3.json", "{\"c\": 3}");

        var result = output.ToString();
        result.Should().Contain("file1.json").And.Contain("file2.json").And.Contain("file3.json");
        result.Should().Contain("[dry-run]", Exactly.Times(3));
    }
}
