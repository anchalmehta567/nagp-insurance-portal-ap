# Insurance Portal - Setup & Deployment Guide

This repo has two parts:
- `WebApp/` - ASP.NET Core app (upload page), runs on EC2
- `LambdaFunction/` - C# Lambda triggered by S3 uploads, writes to RDS MySQL
- `database-setup.sql` - table schema Lambda writes into

---

## 1. Push this code to GitHub

1. Create a new **public** repo on GitHub, e.g. `insurance-portal-app`.
2. On your own machine (or directly via GitHub's web upload if you have no git installed):
   - Easiest no-install method: on the repo page, click **Add file → Upload files**, then drag in all the files/folders from this project, and commit.

---

## 2. Set up the database table

You need network access to RDS (it has no public access). The simplest way is to run this
from your EC2 instance once it exists (see below), or via AWS CloudShell if it can reach your VPC.

```bash
mysql -h <your-rds-endpoint> -P 3306 -u adminDB -p --ssl-mode=REQUIRED < database-setup.sql
```

Replace `<your-rds-endpoint>` with the endpoint you saved from the RDS console
(e.g. `insurance-portal-db.cwz2iy8a2frk.us-east-1.rds.amazonaws.com`). It will prompt for
the master password you set.

---

## 3. Deploy the Web App to EC2

### a) Launch the EC2 instance
- AMI: **Amazon Linux 2023**
- Instance type: `t2.micro` or `t3.micro` (free tier)
- Subnet: one of your **private-app subnets** (no public IP)
- Security Group: `ec2-app-sg`
- IAM instance profile: `EC2-S3-UploadRole`
- Key pair: create/download one if you'll need SSH access (or use **Session Manager**, which
  needs no key pair or open port 22 - recommended, more secure)

### b) Connect to the instance
Use **AWS Systems Manager Session Manager** (EC2 console → select instance → **Connect** →
**Session Manager** tab) - this works even though the instance has no public IP, as long as the
instance role also has SSM permissions (add the AWS managed policy
`AmazonSSMManagedInstanceCore` to `EC2-S3-UploadRole` if Connect fails).

### c) Install .NET 8 and Git on the instance
```bash
sudo yum install -y git
sudo rpm -Uvh https://packages.microsoft.com/config/rhel/9/packages-microsoft-prod.rpm
sudo yum install -y dotnet-sdk-8.0 mysql
```

### d) Clone and run the app
```bash
git clone https://github.com/<your-username>/insurance-portal-app.git
cd insurance-portal-app/WebApp
dotnet publish -c Release -o /home/ec2-user/publish
cd /home/ec2-user/publish
sudo ASPNETCORE_URLS="http://0.0.0.0:80" dotnet InsurancePortalApp.dll
```
(For a persistent background service instead of a manual foreground run, set this up as a
`systemd` service - ask if you'd like those exact steps.)

### e) Update the bucket name in code before publishing
Open `WebApp/Program.cs` and confirm this line has your real bucket name:
```csharp
const string BucketName = "nagp-insurance-portal-uploads-anchal";
```

### f) Test
From a browser: `http://<EC2 private IP>` won't work directly since it's private -
you'll test through the Load Balancer once it's set up (Part 3 later stage). For now, you can
verify the app is running from within the instance itself:
```bash
curl http://localhost/health
```
Should return `Healthy`.

---

## 4. Deploy the Lambda function

### a) Install the Lambda .NET tooling (one-time, on any machine with .NET SDK - can also be done on the EC2 instance)
```bash
dotnet tool install -g Amazon.Lambda.Tools
```

### b) Build and deploy
```bash
cd LambdaFunction
dotnet lambda deploy-function InsurancePortalLambda `
  --function-role arn:aws:iam::<YOUR_ACCOUNT_ID>:role/Lambda-S3-RDS-Role `
  --function-runtime dotnet8 `
  --function-handler InsurancePortalLambda::InsurancePortalLambda.Function::FunctionHandler
```
(On Linux/EC2 shell, replace the backtick line-continuations with `\`.)

### c) Configure the function in the AWS Console after deploying
1. **Configuration → VPC**: attach it to your `insurance-portal-vpc`, select both
   **private-app subnets**, and security group `lambda-sg`.
2. **Configuration → Environment variables**: add
   - `DB_HOST` = your RDS endpoint
   - `DB_NAME` = `insuranceportal`
   - `DB_USER` = `adminDB`
   - `DB_PASSWORD` = your RDS master password
3. **Configuration → General configuration**: set Timeout to 30 seconds (default 3s is often too short
   for a cold-start DB connection).

### d) Add the S3 trigger
1. Go to your S3 bucket → **Properties** tab → **Event notifications** → **Create event notification**.
2. Event name: `OnFileUpload`
3. Event types: check **All object create events**
4. Destination: **Lambda function** → select your deployed function.
5. Save.

---

## 5. Test end-to-end
1. Upload a file through the web app's upload form.
2. Check the S3 bucket - file should appear.
3. Check **CloudWatch → Log groups → /aws/lambda/InsurancePortalLambda** - should show
   log lines for Content-Type, timestamp, and successful insert.
4. Connect to RDS and run:
   ```sql
   SELECT * FROM uploaded_files ORDER BY id DESC;
   ```
   The uploaded file should appear as a new row.

---

## Dependencies
- .NET 8 SDK
- NuGet packages: `AWSSDK.S3`, `MySqlConnector`, `Amazon.Lambda.*` (restored automatically via `dotnet restore`/`dotnet publish`)
