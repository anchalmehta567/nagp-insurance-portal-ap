# Insurance Portal

## Overview

The Insurance Portal is a cloud-native application built on AWS that allows users to upload insurance-related documents through a web interface. Uploaded files are stored in Amazon S3, processed by AWS Lambda, and metadata is stored in Amazon RDS MySQL.

The solution consists of:

- ASP.NET Core (.NET 8) Web Application
- AWS Lambda Function (.NET 8)
- Amazon S3 for document storage
- Amazon RDS MySQL for metadata storage
- AWS Secrets Manager for credential management
- Application Load Balancer and Auto Scaling Group for scalability

---

## Project Structure

```text
insurance-portal-app/
│
├── WebApp/
│   ├── Program.cs
│   ├── InsurancePortalApp.csproj
│   └── wwwroot/
│       ├── index.html
│       ├── css/
│       └── js/
│
├── LambdaFunction/
│   ├── Function.cs
│   └── InsurancePortalLambda.csproj
│
├── database-setup.sql
└── README.md
```

---

# Prerequisites

Before deployment, ensure the following are available:

- AWS Account
- AWS CLI
- .NET 8 SDK
- Git
- Amazon RDS MySQL
- Amazon S3 Bucket
- AWS Lambda
- AWS Secrets Manager

---

# Infrastructure Setup

Create the following AWS resources:

## Networking

- VPC
- Internet Gateway
- Public Subnets (2)
- Private Application Subnets (2)
- Private Database Subnets (2)
- Route Tables
- Security Groups

## Compute

- Launch Template
- Auto Scaling Group
- Application Load Balancer

## Storage & Database

- Amazon S3 Bucket
- Amazon RDS MySQL Instance

## Additional Services

- AWS Lambda Function
- AWS Secrets Manager
- CloudWatch
- VPC Endpoints (S3 and Secrets Manager)

---

# Database Setup

RDS has no public access, so this must be run from an EC2 instance inside the VPC.

Install the MySQL client first — Amazon Linux 2023 ships it under the `mariadb` package name, not `mysql`:

```bash
sudo dnf install -y mariadb105
```

Connect to the RDS instance and create the database:

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

You can also execute the provided:

```text
database-setup.sql
```

file.

**Note:** RDS's security group only allows inbound traffic from the Lambda security group by default. To connect from an EC2 instance to run this SQL, temporarily add an inbound rule allowing the EC2 security group on port 3306, then remove it afterward — only Lambda should be able to reach RDS in the running system.

---

# Secrets Manager Configuration

Create a secret named:

```text
insurance-portal-db-credentials
```

Store the following key-value pairs:

```text
host
dbname
username
password
```

Example:

```text
host = your-rds-endpoint
dbname = insuranceportal
username = adminDB
password = your-password
```

---

# Deploying the Web Application

## Clone Repository

```bash
git clone <repository-url>
cd insurance-portal-app
```

## Publish the Application

```bash
cd WebApp

dotnet restore
dotnet publish -c Release
```

## Deploy on EC2

The web application is deployed using:

- Launch Template
- Auto Scaling Group
- Application Load Balancer

Recommended settings:

```text
AMI: Amazon Linux 2023
Instance Type: t2.micro / t3.micro
Runtime: .NET 8
Auto-assign public IP: Enabled
```

**Note:** Auto-assign public IP must be enabled on instances in the private application subnets. NAT Gateway isn't Free Tier eligible, so without a public IP the instances have no route to install .NET or clone the application code from GitHub. Inbound access remains restricted to the Load Balancer via security groups.

The Launch Template's user data script installs .NET, clones the repository, publishes the application, and runs it as a systemd service:

```bash
#!/bin/bash
export HOME=/root
dnf install -y git
dnf install -y dotnet-sdk-8.0
git clone <repository-url> /home/ec2-user/app
cd /home/ec2-user/app/insurance-portal-app/WebApp
export HOME=/root
dotnet publish -c Release -o /home/ec2-user/publish
systemctl daemon-reload
systemctl enable insuranceportal
systemctl start insuranceportal
```

**Note:** `export HOME=/root` is required because user-data scripts run with no `HOME` environment variable set, which otherwise causes `dotnet publish` to fail with `Required environment variable 'HOME' is not set`. This is only needed inside the boot script, not in an interactive SSH session.

Health Check Endpoint:

```text
/health
```

---

# Deploying the Lambda Function

## Install Lambda Tools

```bash
dotnet tool install -g Amazon.Lambda.Tools
```

## Restore Packages

```bash
cd LambdaFunction

dotnet restore
```

## Deploy Function

```bash
dotnet lambda deploy-function InsurancePortalLambda \
  --function-role arn:aws:iam::<YOUR_ACCOUNT_ID>:role/Lambda-S3-RDS-Role \
  --function-runtime dotnet8 \
  --function-handler InsurancePortalLambda::InsurancePortalLambda.Function::FunctionHandler
```

Recommended configuration:

```text
Memory: 256 MB
Timeout: 30 seconds
Runtime: .NET 8
```

Attach:

```text
Private Application Subnets
lambda-sg Security Group
```

**Note:** Lambda in a private subnet has no internet access and can never be assigned a public IP. A VPC Gateway Endpoint (S3) and a VPC Interface Endpoint (Secrets Manager) must be created so Lambda can reach both services privately. The Interface Endpoint also requires a self-referencing inbound rule on `lambda-sg` allowing HTTPS (443).

---

# Configure S3 Trigger

Open:

```text
S3 Bucket
→ Properties
→ Event Notifications
```

Create Notification:

```text
Event Type: Object Created
Destination: AWS Lambda
Function: InsurancePortalLambda
```

Leave the Suffix filter blank so the trigger fires for all file types, not just one extension.

---

# Testing

## Verify Application

Open the Application Load Balancer DNS name:

```text
http://<alb-dns-name>
```

Upload a test document.

## Verify S3 Upload

Confirm the uploaded file exists in the S3 bucket.

## Verify Lambda Execution

Check CloudWatch Logs:

```text
/aws/lambda/InsurancePortalLambda
```

## Verify Database Record

Run:

```sql
SELECT * 
FROM uploaded_files
ORDER BY id DESC;
```

The uploaded file should appear in the results.

---

# Dependencies

## Web Application

```text
.NET 8 SDK
AWSSDK.S3
AWSSDK.Extensions.NETCore.Setup
```

## Lambda Function

```text
Amazon.Lambda.Core
Amazon.Lambda.S3Events
Amazon.Lambda.Serialization.SystemTextJson
AWSSDK.SecretsManager
MySqlConnector
```

---

# Known Limitations

- Single AWS Region deployment
- Single-AZ RDS deployment
- NAT Gateway not deployed due to AWS Free Tier constraints
- HTTPS and custom domain not configured
- Intended as an MVP / proof-of-concept implementation

---

# Cleanup

To avoid unnecessary AWS charges:

1. Delete the Auto Scaling Group
2. Delete EC2 instances
3. Delete the Application Load Balancer
4. Delete the Lambda function
5. Delete RDS
6. Empty and delete the S3 bucket
7. Delete Secrets Manager secret
8. Remove CloudWatch resources
9. Delete VPC resources

---
