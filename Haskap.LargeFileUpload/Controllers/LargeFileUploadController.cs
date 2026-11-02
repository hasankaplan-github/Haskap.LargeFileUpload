using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System.IO.Pipelines;

namespace Haskap.LargeFileUpload.Controllers;

public class LargeFileUploadController : Controller
{
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly ILogger<LargeFileUploadController> _logger;
    private readonly LargeFileUploadSettings _largeFileUploadSettings;

    public LargeFileUploadController(
        IWebHostEnvironment webHostEnvironment,
        ILogger<LargeFileUploadController> logger,
        IOptions<LargeFileUploadSettings> largeFileUploadSettingsOptions)
    {
        _webHostEnvironment = webHostEnvironment;
        _logger = logger;
        _largeFileUploadSettings = largeFileUploadSettingsOptions.Value;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpPost]
    public async Task<JsonResult> Init(CancellationToken cancellationToken = default)
    {
        return Json(new
        {
            LargeFileUploadSessionId = Guid.NewGuid(),
            ChunkSizeInBytes = _largeFileUploadSettings.ChunkSizeInBytes
        });
    }

    [HttpPost]
    public async Task UploadChunk()
    {
        if (!Request.ContentType?.StartsWith("multipart/form-data") ?? true)
        {
            throw new BadHttpRequestException("The request does not contain valid multipart form data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(Request.ContentType).Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new BadHttpRequestException("Missing boundary in multipart form data.");
        }

        var cancellationToken = HttpContext.RequestAborted;

        await SaveViaMultipartReaderAsync(
            boundary,
            Request.Body,
            cancellationToken);
        //await SaveViaPipeReaderAsync(Request.BodyReader, largeFileUploadSessionId, currentFileIndex, currentChunkIndex, cancellationToken);
    }

    [HttpPost]
    public async Task Finalize(Guid largeFileUploadSessionId, string currentFileExtension, CancellationToken cancellationToken = default)
    {
        var inputDirectoryPath = Path.Combine(_webHostEnvironment.ContentRootPath, "Assets", "LargeFileUpload");
        var inputFileNamePattern = $"{largeFileUploadSessionId}_*";
        var outputFullFileName = Path.Combine(inputDirectoryPath, $"{largeFileUploadSessionId}.{currentFileExtension}");

        await CombineMultipleFilesIntoSingleFileAsync(inputDirectoryPath, inputFileNamePattern, outputFullFileName, cancellationToken);
        _logger.LogInformation("File upload completed.");
    }

    [HttpPost]
    public async Task CancelCurrent(Guid largeFileUploadSessionId, CancellationToken cancellationToken = default)
    {
        var inputDirectoryPath = Path.Combine(_webHostEnvironment.ContentRootPath, "Assets", "LargeFileUpload");
        var inputFileNamePattern = $"{largeFileUploadSessionId}*";

        await DeleteCurrentRelatedFilesAsync(inputDirectoryPath, inputFileNamePattern, cancellationToken);
    }

    [HttpPost]
    public async Task CancelAll(IList<Guid> processedLargeFileUploadSessionIds, CancellationToken cancellationToken = default)
    {
        var inputDirectoryPath = Path.Combine(_webHostEnvironment.ContentRootPath, "Assets", "LargeFileUpload");

        Task[] tasks = [];
        foreach (var id in processedLargeFileUploadSessionIds)
        {
            var inputFileNamePattern = $"{id}*";
            tasks.Append(DeleteCurrentRelatedFilesAsync(inputDirectoryPath, inputFileNamePattern, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }


    private const int BufferSize = 16 * 1024 * 1024; // 16 MB buffer size

    private async Task SaveViaPipeReaderAsync(PipeReader contentReader, Guid largeFileUploadSessionId, int currentFileIndex, int currentChunkIndex, CancellationToken cancellationToken)
    {
        string targetFullFileName = Path.Combine(_webHostEnvironment.ContentRootPath, "Assets", "LargeFileUpload", $"test_{currentFileIndex}_{currentChunkIndex}.dat");
        long totalBytesRead = 0;

        using FileStream outputFileStream = new FileStream(
            path: targetFullFileName,
            mode: FileMode.OpenOrCreate,
            access: FileAccess.Write,
            share: FileShare.None,
            bufferSize: BufferSize,
            useAsync: true);

        while (true)
        {
            var readResult = await contentReader.ReadAsync(cancellationToken);
            var buffer = readResult.Buffer;

            foreach (var memory in buffer)
            {
                await outputFileStream.WriteAsync(memory, cancellationToken);
                totalBytesRead += memory.Length;
            }
            contentReader.AdvanceTo(buffer.End);

            if (readResult.IsCompleted)
            {
                break;
            }
        }

        _logger.LogInformation("File upload completed (via pipeReader). Total bytes read: {TotalBytesRead} bytes.", totalBytesRead);
    }

    private async Task SaveViaMultipartReaderAsync(string boundary, Stream contentStream, CancellationToken cancellationToken)
    {
        string targetFilePath = Path.Combine(_webHostEnvironment.ContentRootPath, "Assets", "LargeFileUpload");
        string targetFullFileName = Path.Combine(targetFilePath, $"temp_{Guid.NewGuid()}.dat");
        //CheckAndRemoveLocalFile(targetFilePath);

        var formData = new Dictionary<string, string>();

        var reader = new MultipartReader(boundary, contentStream);
        MultipartSection? section;
        long totalBytesRead = 0;

        // Process each section in the multipart body
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) != null)
        {
            var contentDisposition = section.GetContentDispositionHeader();

            // Check if the section is a file
            if (contentDisposition != null && contentDisposition.IsFileDisposition())
            {
                try
                {
                    await using (FileStream outputFileStream = new FileStream(
                                                                    path: targetFullFileName,
                                                                    mode: FileMode.Create,
                                                                    access: FileAccess.Write,
                                                                    share: FileShare.None,
                                                                    bufferSize: BufferSize,
                                                                    useAsync: true))
                    {
                        _logger.LogInformation($"Processing file: {contentDisposition.FileName.Value}");
                        // Write the file content to the target file
                        await section.Body.CopyToAsync(outputFileStream, cancellationToken);
                        totalBytesRead += section.Body.Length;
                    }
                }
                catch (Exception)
                {
                    System.IO.File.Delete(targetFullFileName);
                    throw;
                }
            }
            else if (contentDisposition != null && contentDisposition.IsFormDisposition())
            {
                // Handle metadata (form fields)
                string key = contentDisposition.Name.Value!;
                using var streamReader = new StreamReader(section.Body);
                string value = await streamReader.ReadToEndAsync(cancellationToken);
                formData.Add(key, value);
                _logger.LogInformation("Received metadata: {key} = {value}", key, value);
            }
        }

        //await using (var outputStream = new FileStream(
        //                                        path: Path.Combine(targetFilePath, "test.dat"),
        //                                        mode: FileMode.Append,
        //                                        access: FileAccess.Write,
        //                                        share: FileShare.None,
        //                                        bufferSize: BufferSize,
        //                                        useAsync: true))
        //{
        //    await using (var inputStream = System.IO.File.OpenRead(targetFullFileName))
        //    {
        //        // Buffer size can be passed as the second argument.
        //        await inputStream.CopyToAsync(outputStream, cancellationToken);
        //    }
        //}

        //System.IO.File.Delete(targetFullFileName);


        System.IO.File.Move(targetFullFileName, Path.Combine(targetFilePath, $"{formData["largeFileUploadSessionId"]}_{formData["currentFileIndex"]}_{formData["currentChunkIndex"]}_.dat"));

        _logger.LogInformation("File chunk upload completed (via multipart). Total bytes read: {TotalBytesRead} bytes.", totalBytesRead);
    }

    private async Task CombineMultipleFilesIntoSingleFileAsync(string inputDirectoryPath, string inputFileNamePattern, string outputFullFileName, CancellationToken cancellationToken)
    {
        string[] inputFilePaths = Directory.GetFiles(inputDirectoryPath, inputFileNamePattern)
            //.OrderBy(x => int.Parse(x.Split('_', 3)[1]))
            .OrderBy(x => int.Parse(x.Split('_', 4)[2]))
            .ToArray();
        _logger.LogInformation("Number of files: {NumberOfFiles}.", inputFilePaths.Length);
        //await using (var outputStream = System.IO.File.Create(outputFullFileName))
        await using (var outputStream = new FileStream(
                                            path: outputFullFileName,
                                            mode: FileMode.Append,
                                            access: FileAccess.Write,
                                            share: FileShare.None,
                                            bufferSize: BufferSize,
                                            useAsync: true))
        {
            foreach (var inputFilePath in inputFilePaths)
            {
                await using (var inputStream = System.IO.File.OpenRead(inputFilePath))
                {
                    // Buffer size can be passed as the second argument.
                    await inputStream.CopyToAsync(outputStream, cancellationToken);
                    inputStream.Close();
                }
                _logger.LogInformation("The file {File} has been processed.", inputFilePath);

                System.IO.File.Delete(inputFilePath);
            }

            outputStream.Close();
        }
    }

    private async Task DeleteCurrentRelatedFilesAsync(string inputDirectoryPath, string inputFileNamePattern, CancellationToken cancellationToken)
    {
        string[] inputFilePaths = Directory.GetFiles(inputDirectoryPath, inputFileNamePattern);

        foreach (var inputFilePath in inputFilePaths)
        {
            System.IO.File.Delete(inputFilePath);
            _logger.LogInformation("The file {File} has been deleted.", inputFilePath);
        }
    }
}
