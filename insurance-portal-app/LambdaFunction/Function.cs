using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using MySqlConnector;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace InsurancePortalLambda;

public class Function
{
    private static readonly IAmazonS3 S3Client = new AmazonS3Client();

    // These are read from Lambda Environment Variables (set in the AWS Console
    // when you create the function - see setup instructions).
    private static readonly string DbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "";
    private static readonly string DbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "insuranceportal";
    private static readonly string DbUser = Environment.GetEnvironmentVariable("DB_USER") ?? "";
    private static readonly string DbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";

    /// <summary>
    /// Entry point triggered by an S3 "ObjectCreated" event whenever a file
    /// is uploaded to the insurance portal's S3 bucket.
    /// </summary>
    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        foreach (var record in s3Event.Records)
        {
            var bucketName = record.S3.Bucket.Name;
            var objectKey = Uri.UnescapeDataString(record.S3.Object.Key);

            context.Logger.LogInformation($"Processing file '{objectKey}' from bucket '{bucketName}'");

            try
            {
                // 1. Read the uploaded object's metadata to get Content-Type
                var metadataResponse = await S3Client.GetObjectMetadataAsync(bucketName, objectKey);
                string contentType = metadataResponse.Headers.ContentType ?? "unknown";
                DateTime uploadTimestamp = DateTime.UtcNow;

                context.Logger.LogInformation($"File: {objectKey} | Content-Type: {contentType} | Timestamp: {uploadTimestamp:O}");

                // 2. Insert the file info into RDS MySQL
                await InsertFileRecordAsync(objectKey, contentType, uploadTimestamp, context);

                context.Logger.LogInformation($"Successfully recorded '{objectKey}' in the database.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error processing file '{objectKey}': {ex.Message}");
                throw; // rethrow so Lambda reports the invocation as failed (visible in CloudWatch)
            }
        }
    }

    private static async Task InsertFileRecordAsync(
        string fileName, string contentType, DateTime uploadTimestamp, ILambdaContext context)
    {
        var connectionString =
            $"Server={DbHost};Database={DbName};User={DbUser};Password={DbPassword};SslMode=Required;";

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        const string sql = @"
            INSERT INTO uploaded_files (file_name, content_type, upload_timestamp)
            VALUES (@fileName, @contentType, @uploadTimestamp);";

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@fileName", fileName);
        command.Parameters.AddWithValue("@contentType", contentType);
        command.Parameters.AddWithValue("@uploadTimestamp", uploadTimestamp);

        int rowsAffected = await command.ExecuteNonQueryAsync();
        context.Logger.LogInformation($"Inserted {rowsAffected} row(s) into uploaded_files table.");
    }
}
