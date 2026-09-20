-- Run this once, connected to your RDS MySQL instance,
-- before testing the Lambda function.

-- If the "Initial database name" field was left blank when creating RDS,
-- create the database first:
CREATE DATABASE IF NOT EXISTS insuranceportal;

USE insuranceportal;

CREATE TABLE IF NOT EXISTS uploaded_files (
    id INT AUTO_INCREMENT PRIMARY KEY,
    file_name VARCHAR(255) NOT NULL,
    content_type VARCHAR(100),
    upload_timestamp DATETIME NOT NULL
);

-- Quick check after Lambda has run at least once:
-- SELECT * FROM uploaded_files ORDER BY id DESC;
