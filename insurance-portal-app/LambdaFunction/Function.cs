using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using MySqlConnector;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace InsurancePortalLambda;

public class Function
{
    private static readonly IAmazonS3 S3Client = new AmazonS3Client();
    private static readonly IAmazonSecretsManager SecretsClient = new AmazonSecretsManagerClient();

    // Name of the secret in AWS Secrets Manager holding the DB credentials
    private static readonly string SecretName =
        Environment.GetEnvironmentVariable("DB_SECRET_NAME") ?? "insurance-portal-db-credentials";

    /// <summary>
    /// Entry point triggered by an S3 "ObjectCreated" event whenever a file
    /// is uploaded to the insurance portal's S3 bucket.
    /// </summary>
    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        // Fetch DB credentials from Secrets Manager once per invocation.
        // We log confirmation of retrieval (non-sensitive fields only) to
        // satisfy the bonus requirement of "showing it in the console".
        var dbCreds = await GetDbCredentialsAsync(context);

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

                // 2. Insert the file info into RDS MySQL, using credentials from Secrets Manager
                await InsertFileRecordAsync(objectKey, contentType, uploadTimestamp, dbCreds, context);

                context.Logger.LogInformation($"Successfully recorded '{objectKey}' in the database.");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error processing file '{objectKey}': {ex.Message}");
                throw; // rethrow so Lambda reports the invocation as failed (visible in CloudWatch)
            }
        }
    }

    /// <summary>
    /// Retrieves the DB connection secret from AWS Secrets Manager.
    /// Logs confirmation of a successful fetch and the non-sensitive fields
    /// (username, host, dbname) to CloudWatch - never the password itself.
    /// </summary>
    private static async Task<DbCredentials> GetDbCredentialsAsync(ILambdaContext context)
    {
        context.Logger.LogInformation($"Fetching DB credentials from Secrets Manager secret '{SecretName}'...");

        var request = new GetSecretValueRequest { SecretId = SecretName };
        var response = await SecretsClient.GetSecretValueAsync(request);

        var secretJson = JsonDocument.Parse(response.SecretString);
        var root = secretJson.RootElement;

        var creds = new DbCredentials
        {
            Host = root.GetProperty("host").GetString() ?? "",
            DbName = root.GetProperty("dbname").GetString() ?? "",
            Username = root.GetProperty("username").GetString() ?? "",
            Password = root.GetProperty("password").GetString() ?? ""
        };

        // Bonus requirement: show retrieval in the Lambda console/CloudWatch logs.
        // Password is intentionally never logged.
        context.Logger.LogInformation(
            $"Secrets Manager: retrieved credentials successfully -> " +
            $"username='{creds.Username}', host='{creds.Host}', dbname='{creds.DbName}', " +
            $"secretArn='{response.ARN}', versionId='{response.VersionId}'");

        return creds;
    }

    private static async Task InsertFileRecordAsync(
        string fileName, string contentType, DateTime uploadTimestamp, DbCredentials dbCreds, ILambdaContext context)
    {
        var connectionString =
            $"Server={dbCreds.Host};Database={dbCreds.DbName};User={dbCreds.Username};Password={dbCreds.Password};SslMode=Required;";

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

/// <summary>
/// Plain data holder for DB connection details retrieved from Secrets Manager.
/// </summary>
public class DbCredentials
{
    public string Host { get; set; } = "";
    public string DbName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
