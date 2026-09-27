using Amazon.S3;
using Amazon.S3.Transfer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonS3>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/upload", async (IFormFile file, IAmazonS3 s3Client) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "No file selected." });
    }

    var bucketName = "nagp-insurance-portal-uploads-anchal";
    var key = $"{Guid.NewGuid()}_{file.FileName}";

    using var stream = file.OpenReadStream();

    var transferUtility = new TransferUtility(s3Client);
    await transferUtility.UploadAsync(stream, bucketName, key);

    return Results.Ok(new
    {
        message = "File uploaded successfully.",
        fileName = file.FileName,
        s3Key = key
    });
})
.DisableAntiforgery(); 

// Simple health check endpoint (useful for ALB target group health checks)
app.MapGet("/health", () => Results.Ok("Healthy"));

app.Run();
