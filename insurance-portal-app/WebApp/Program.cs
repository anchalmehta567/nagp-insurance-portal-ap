using Amazon.S3;
using Amazon.S3.Transfer;

// ============================================================
// IMPORTANT: set this to your actual S3 bucket name before deploying
// ============================================================
const string BucketName = "nagp-insurance-portal-uploads-anchal";

const string HtmlPage = @"
<!DOCTYPE html>
<html>
<head>
    <title>Insurance Portal - Document Upload</title>
    <style>
        body { font-family: Arial, sans-serif; max-width: 500px; margin: 60px auto; padding: 20px; }
        h1 { color: #2c3e50; }
        input[type=file] { margin: 15px 0; display: block; }
        button {
            background: #2c3e50; color: white; border: none;
            padding: 10px 20px; cursor: pointer; border-radius: 4px;
        }
        button:hover { background: #1a252f; }
    </style>
</head>
<body>
    <h1>Insurance Document Upload</h1>
    <p>Upload identity proofs, claim forms, or supporting evidence.</p>
    <form action=""/upload"" method=""post"" enctype=""multipart/form-data"">
        <input type=""file"" name=""file"" required />
        <button type=""submit"">Upload</button>
    </form>
</body>
</html>";

var builder = WebApplication.CreateBuilder(args);

// Register the S3 client. On EC2, this automatically uses the
// IAM Role attached to the instance (EC2-S3-UploadRole) - no
// access keys needed anywhere in this code.
builder.Services.AddAWSService<IAmazonS3>();

var app = builder.Build();

// -------------------- Home page (upload form) --------------------
app.MapGet("/", () => Results.Content(HtmlPage, "text/html"));

// -------------------- Upload endpoint --------------------
app.MapPost("/upload", async (IFormFile file, IAmazonS3 s3Client) =>
{
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest("Please choose a file before uploading.");
    }

    try
    {
        using var stream = file.OpenReadStream();

        var transferUtility = new TransferUtility(s3Client);
        var uploadRequest = new TransferUtilityUploadRequest
        {
            InputStream = stream,
            Key = $"{Guid.NewGuid()}_{file.FileName}",
            BucketName = BucketName,
            ContentType = file.ContentType
        };

        await transferUtility.UploadAsync(uploadRequest);

        return Results.Content(
            $"<h2>Upload successful!</h2>" +
            $"<p>File <strong>{file.FileName}</strong> was uploaded to S3.</p>" +
            $"<p>Content-Type: {file.ContentType}</p>" +
            $"<a href=\"/\">Upload another file</a>",
            "text/html");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Upload failed: {ex.Message}");
    }
}).DisableAntiforgery();

// Simple health check endpoint - useful for the Load Balancer / Target Group
app.MapGet("/health", () => Results.Ok("Healthy"));

app.Run();
