using System.Diagnostics;
using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace unzip_service.Controllers;

[ApiController]
[Route("api/unzip")]
public class UnzipController : ControllerBase
{
    private const long MaxUploadBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan SevenZipTimeout = TimeSpan.FromSeconds(60);
    
    public class ExtractRequest
    {
        public IFormFile? File { get; set; }
        public string? Password { get; set; }
    }

    [HttpPost("extract")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> Extract([FromForm] ExtractRequest request)
    {
        var file = request?.File;
        var password = request?.Password;

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "NO_FILE", message = "Send 'file' (multipart/form-data)." });

        var jobId = Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), "extract-api", jobId);
        var inDir = Path.Combine(baseDir, "in");
        var outDir = Path.Combine(baseDir, "out");

        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var inputPath = Path.Combine(inDir, SanitizeFileName(file.FileName));
        var resultZipPath = Path.Combine(baseDir, "result.zip");

        try
        {
            await using (var fs = System.IO.File.Create(inputPath))
                await file.CopyToAsync(fs);

            var testArgs = $"t \"{inputPath}\" {(string.IsNullOrWhiteSpace(password) ? "" : $"-p\"{password}\"")} -y";
            var test = await Run7zAsync(testArgs);

            if (test.TimedOut)
                return StatusCode(504, new { error = "TIMEOUT", message = "Extraction timed out." });

            if (test.ExitCode != 0)
            {
                var (code, payload) = Classify7zError(test.StdOut + "\n" + test.StdErr, passwordProvided: !string.IsNullOrWhiteSpace(password));
                return StatusCode(code, payload);
            }

            var extractArgs = BuildExtractArgs(inputPath, outDir, password);
            var extract = await Run7zAsync(extractArgs);

            if (extract.TimedOut)
                return StatusCode(504, new { error = "TIMEOUT", message = "Extraction timed out." });

            if (extract.ExitCode != 0)
            {
                var (code, payload) = Classify7zError(extract.StdOut + "\n" + extract.StdErr, passwordProvided: !string.IsNullOrWhiteSpace(password));
                return StatusCode(code, payload);
            }

            var extractedFiles = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);

            if (extractedFiles.Length == 0)
            {
                return StatusCode(422, new { error = "NO_OUTPUT", message = "No files were extracted." });
            }

            if (extractedFiles.Length == 1)
            {
                var singlePath = extractedFiles[0];
                var fileBytes = await System.IO.File.ReadAllBytesAsync(singlePath);
                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(singlePath, out var contentType))
                    contentType = "application/octet-stream";

                Response.Headers["X-Job-Id"] = jobId;
                return File(fileBytes, contentType, Path.GetFileName(singlePath));
            }

            if (System.IO.File.Exists(resultZipPath))
                System.IO.File.Delete(resultZipPath);

            ZipFile.CreateFromDirectory(outDir, resultZipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            var zipBytes = await System.IO.File.ReadAllBytesAsync(resultZipPath);

            Response.Headers["X-Job-Id"] = jobId;
            return File(zipBytes, "application/zip", "extracted.zip");
        }
        finally
        {
            try
            {
                if (Directory.Exists(baseDir))
                    Directory.Delete(baseDir, recursive: true);
            }
            catch { }
        }
    }

    private static string BuildExtractArgs(string inputPath, string outDir, string? password)
    {
        var passArg = string.IsNullOrWhiteSpace(password) ? "" : $"-p\"{password}\"";
        return $"x \"{inputPath}\" -o\"{outDir}\" {passArg} -y";
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "upload.bin" : name;
    }

    private static (int httpCode, object payload) Classify7zError(string output, bool passwordProvided)
    {
        var text = (output ?? "").ToLowerInvariant();

        if (text.Contains("wrong password") || text.Contains("password is incorrect"))
            return (401, new { error = "INVALID_PASSWORD", message = "Password is incorrect." });

        if (!passwordProvided && (text.Contains("encrypted") || text.Contains("password")))
            return (400, new { error = "PASSWORD_REQUIRED", message = "Archive is encrypted. Provide 'password'." });

        if (text.Contains("can not open the file as archive") || text.Contains("is not archive"))
            return (415, new { error = "UNSUPPORTED_FORMAT", message = "File is not a supported archive format." });

        if (text.Contains("data error") || text.Contains("unexpected end of archive") || text.Contains("headers error"))
            return (422, new { error = "CORRUPT_ARCHIVE", message = "Archive appears corrupted." });

        return (400, new { error = "EXTRACT_FAILED", message = "Failed to extract archive.", details = output });
    }

    private static async Task<(int ExitCode, bool TimedOut, string StdOut, string StdErr)> Run7zAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "7z",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return (-1, false, "", $"Failed to start 7z. Is it installed and on PATH? {ex.Message}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var waitTask = proc.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(SevenZipTimeout)) == waitTask;

        if (!completed)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, true, await stdoutTask, await stderrTask);
        }

        return (proc.ExitCode, false, await stdoutTask, await stderrTask);
    }
}