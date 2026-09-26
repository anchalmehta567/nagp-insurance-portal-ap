async function uploadFile() {
    const fileInput = document.getElementById("fileInput");
    const status = document.getElementById("status");

    if (!fileInput.files || fileInput.files.length === 0) {
        status.textContent = "Please select a file first.";
        return;
    }

    const file = fileInput.files[0];
    const formData = new FormData();
    formData.append("file", file);

    status.textContent = "Uploading...";

    try {
        const response = await fetch("/api/upload", {
            method: "POST",
            body: formData
        });

        const result = await response.json();

        if (response.ok) {
            status.textContent = `Uploaded: ${result.fileName}`;
        } else {
            status.textContent = `Error: ${result.message || "Upload failed."}`;
        }
    } catch (err) {
        status.textContent = "Upload failed. Please try again.";
        console.error(err);
    }
}
