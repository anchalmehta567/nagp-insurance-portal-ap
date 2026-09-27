# Insurance Portal - Setup & Deployment Guide

This repo has two parts:
- `WebApp/` - ASP.NET Core app (upload page + `/api/upload` endpoint), runs on EC2 behind an Auto Scaling Group and Application Load Balancer
- `LambdaFunction/` - C# Lambda triggered by S3 uploads; reads file Content-Type, fetches DB credentials from AWS Secrets Manager, and writes a record to RDS MySQL
- `database-setup.sql` - table schema Lambda writes into

---

## Architecture summary

- **VPC**: `insurance-portal-vpc` (10.0.0.0/16), 6 subnets across 2 AZs (public / private-app / private-db)
- **EC2**: launched via a Launch Template + Auto Scaling Group (not a single manual instance) behind an Application Load Balancer
- **S3**: `nagp-insurance-portal-uploads-anchal` - stores uploads, triggers Lambda on `ObjectCreated`
- **Lambda**: `InsurancePortalLambda`, deployed inside the VPC (private-app subnets), retrieves DB credentials from Secrets Manager at runtime
- **RDS**: MySQL 8.4, `db.t3.micro`, Single-AZ, no public access, only reachable from Lambda's security group
- **VPC Endpoints**: an S3 Gateway Endpoint and a Secrets Manager Interface Endpoint, since Lambda's private subnets have no internet route

---

## 1. Source code location

This project's source code is included in this submission. If deploying fresh, push it to a GitHub repository first, since the EC2 launch template's user-data script (see Section 3) clones the application code directly from GitHub at instance boot time.

---

## 2. Set up the database table

RDS has no public access, so run this from an EC2 instance inside the VPC (any instance in a private-app subnet works, including the ASG's own instances).

Install the MySQL client (Amazon Linux 2023 ships it under the `mariadb` package name, not `mysql`):
```bash
sudo dnf install -y mariadb105
```

Then connect and run the schema:
```bash
mysql -h <your-rds-endpoint> -P 3306 -u adminDB -p
```
```sql
CREATE DATABASE IF NOT EXISTS insuranceportal;
USE insuranceportal;
CREATE TABLE IF NOT EXISTS uploaded_files (
    id INT AUTO_INCREMENT PRIMARY KEY,
    file_name VARCHAR(255) NOT NULL,
    content_type VARCHAR(100),
    upload_timestamp DATETIME NOT NULL
);
```

**Note:** RDS's security group only allows inbound traffic from the Lambda security group by default. To run the SQL above from an EC2 instance, temporarily add an inbound rule allowing the EC2 security group on port 3306, then remove it afterward - only Lambda should be able to reach RDS in the running system.

Replace `<your-rds-endpoint>` with your actual RDS endpoint (RDS console -> Databases -> your instance -> Connectivity & security tab).

---

## 3. Deploy the Web App (via Launch Template + Auto Scaling Group)

The app is **not** deployed by manually launching a single EC2 instance. It's deployed through a **Launch Template** whose **user-data script** automatically installs everything and starts the app as a background service on every instance the Auto Scaling Group creates.

### a) Launch Template settings
- AMI: **Amazon Linux 2023**
- Instance type: `t2.micro` / `t3.micro` (Free Tier)
- Subnet: left unspecified in the template (the ASG places instances across both private-app subnets)
- Security Group: `ec2-app-sg`
- IAM instance profile: `EC2-S3-UploadRole`
- **Auto-assign public IP: Enabled** - required because NAT Gateway isn't Free Tier eligible; without a public IP, instances in the private subnet cannot reach the internet to install packages (see Scope and Assumptions document for this trade-off)

### b) User-data script (the actual deployment mechanism)
```bash
#!/bin/bash
export HOME=/root
dnf install -y git
dnf install -y dotnet-sdk-8.0
git clone https://github.com/anchalmehta567/nagp-insurance-portal-ap.git /home/ec2-user/app
cd /home/ec2-user/app/insurance-portal-app/WebApp
export HOME=/root
dotnet publish -c Release -o /home/ec2-user/publish
cat << 'EOF' > /etc/systemd/system/insuranceportal.service
[Unit]
Description=Insurance Portal Web App
After=network.target

[Service]
WorkingDirectory=/home/ec2-user/publish
ExecStart=/usr/bin/dotnet /home/ec2-user/publish/InsurancePortalApp.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_URLS=http://0.0.0.0:80
Environment=ASPNETCORE_ENVIRONMENT=Production
SyslogIdentifier=insuranceportal

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable insuranceportal
systemctl start insuranceportal
```

**Important:** `export HOME=/root` is required here because cloud-init/user-data scripts run in a minimal environment with no `HOME` variable set, which causes `dotnet publish` to fail with `System.InvalidOperationException: Required environment variable 'HOME' is not set`. This is only needed inside this boot script - not when manually running commands in an interactive SSH/Instance Connect session.

### c) Auto Scaling Group settings
- Subnets: both private-app subnets (`private-app-subnet-az1`, `private-app-subnet-az2`)
- Attached to: `insurance-portal-alb` via target group `insurance-portal-tg`
- Health checks: EC2 + ELB, grace period 300 seconds
- Capacity: min 1 / desired 2 / max 4
- Scaling policy: target-tracking on 50% average CPU utilization

### d) Application Load Balancer settings
- Internet-facing, in both public subnets
- Security group: `alb-sg`
- Listener: HTTP:80 -> forwards to `insurance-portal-tg`
- Target group health check path: `/health`

### e) Bucket name in code
`WebApp/Program.cs` reads the bucket name as a hardcoded constant (not an environment variable, to avoid a misconfiguration risk):
```csharp
var bucketName = "nagp-insurance-portal-uploads-anchal";
```
Confirm this matches your actual bucket name before publishing.

### f) Manually redeploying to already-running instances (without a full Instance Refresh)
If you update the code and want to push it to instances that are already running (faster than a full ASG instance refresh):
```bash
cd ~
sudo rm -rf /home/ec2-user/app
git clone https://github.com/anchalmehta567/nagp-insurance-portal-ap.git ~/app
cd ~/app/insurance-portal-app/WebApp
sudo dotnet publish -c Release -o /home/ec2-user/publish
sudo systemctl restart insuranceportal
sudo systemctl status insuranceportal
```
Do this on **every** currently-running instance behind the ALB, since the load balancer will route to whichever instance it picks.

### g) Test
```bash
curl http://localhost/health
```
Should return `Healthy`. Once the ALB and target group are set up, test through the ALB's public DNS name (EC2 -> Load Balancers -> insurance-portal-alb -> DNS name).

---

## 4. Deploy the Lambda function

### a) Install the Lambda .NET tooling
Can be done on any EC2 instance with the .NET SDK already installed (they all have it, from the user-data script):
```bash
dotnet tool install -g Amazon.Lambda.Tools
export PATH="$PATH:/home/ec2-user/.dotnet/tools"
```

### b) AWS CLI credentials for deployment
The EC2 instance's own role (`EC2-S3-UploadRole`) does not have Lambda deployment permissions. Configure the CLI with your IAM user's access key temporarily instead:
```bash
aws configure
```
Enter your IAM user's Access Key ID, Secret Access Key, region `us-east-1`, and leave output format blank.

### c) Build and deploy
```bash
cd ~/app/insurance-portal-app/LambdaFunction
dotnet lambda deploy-function InsurancePortalLambda \
  --function-role arn:aws:iam::<YOUR_ACCOUNT_ID>:role/Lambda-S3-RDS-Role \
  --function-runtime dotnet8 \
  --function-handler InsurancePortalLambda::InsurancePortalLambda.Function::FunctionHandler
```
When prompted interactively for Memory Size / Timeout / VPC subnets / VPC security groups, use:
- Memory: `256`
- Timeout: `30`
- Subnets: both private-app subnet IDs
- Security group: `lambda-sg`

### d) Configure Secrets Manager (bonus activity - replaces plain environment variables)
Instead of storing `DB_HOST` / `DB_USER` / `DB_PASSWORD` as plaintext environment variables, credentials are stored in a Secrets Manager secret and fetched by the function at runtime.

1. **Secrets Manager -> Store a new secret -> Other type of secret**, with key-value pairs:
   - `host` = your RDS endpoint
   - `dbname` = `insuranceportal`
   - `username` = `adminDB`
   - `password` = your RDS master password
2. Name it `insurance-portal-db-credentials`.
3. The Lambda role (`Lambda-S3-RDS-Role`) needs `secretsmanager:GetSecretValue` - already included in its inline policy.
4. No environment variables are required for DB connection info; the function reads the secret by name (`insurance-portal-db-credentials`, overridable via the `DB_SECRET_NAME` environment variable if you rename it).

### e) VPC configuration (required - Lambda has no internet access)
Configuration -> VPC: attach to `insurance-portal-vpc`, both private-app subnets, security group `lambda-sg`.

Since Lambda in a private subnet cannot reach any AWS service over the public internet (and can never be assigned a public IP, unlike EC2), you must also create:

**S3 Gateway Endpoint** (free):
- VPC -> Endpoints -> Create endpoint -> service `com.amazonaws.us-east-1.s3` (Type: Gateway)
- Route table: `private-app-rt`

**Secrets Manager Interface Endpoint** (small hourly cost):
- VPC -> Endpoints -> Create endpoint -> service `com.amazonaws.us-east-1.secretsmanager` (Type: Interface)
- Subnets: both private-app subnets
- Security group: `lambda-sg`
- `lambda-sg` also needs a self-referencing inbound rule allowing HTTPS (443) from itself, since Interface Endpoints require inbound access from the resources using them

### f) General configuration
Timeout: 30 seconds (default 3s is too short for a cold-start Secrets Manager fetch + DB connection).

### g) Add the S3 trigger
1. S3 bucket -> **Properties** tab -> **Event notifications** -> **Create event notification**.
2. Event name: `OnFileUpload`
3. Event types: **All object create events** (leave the Suffix filter blank - do not restrict to a single file extension)
4. Destination: **Lambda function** -> `InsurancePortalLambda`
5. Save.

---

## 5. Test end-to-end

1. Upload a file through the web app's upload form (via the ALB's DNS name).
2. Check the S3 bucket - the file should appear.
3. Check **CloudWatch -> Log groups -> /aws/lambda/InsurancePortalLambda** - the newest log stream should show:
   ```
   Fetching DB credentials from Secrets Manager secret 'insurance-portal-db-credentials'...
   Secrets Manager: retrieved credentials successfully -> username='adminDB', host='...', ...
   Processing file '...' from bucket '...'
   Inserted 1 row(s) into uploaded_files table.
   Successfully recorded '...' in the database.
   ```
4. Connect to RDS and run:
   ```sql
   SELECT * FROM uploaded_files ORDER BY id DESC;
   ```
   The uploaded file should appear as a new row.

---

## Common issues encountered while building this (and their fixes)

- **`HOME` environment variable not set** during `dotnet publish` inside the user-data script -> fixed by `export HOME=/root` before running any `dotnet` commands in that script specifically.
- **Lambda timing out at exactly 30 seconds** on every invocation -> caused by Lambda having no route to S3 or Secrets Manager from inside a private subnet -> fixed by adding the VPC Gateway/Interface Endpoints described above.
- **`mysql: command not found`** on fresh Amazon Linux 2023 instances -> the package is named `mariadb105` (or `mariadb`), not `mysql`.
- **Security groups / NACLs occasionally misconfigured in the wrong VPC or with swapped subnet associations** during manual setup - always verify the VPC ID and subnet associations shown in the console match the intended resource before relying on a rule.

---

## Dependencies
- .NET 8 SDK
- NuGet packages: `AWSSDK.S3`, `AWSSDK.Extensions.NETCore.Setup`, `AWSSDK.SecretsManager`, `MySqlConnector`, `Amazon.Lambda.*` (restored automatically via `dotnet restore` / `dotnet publish`)
