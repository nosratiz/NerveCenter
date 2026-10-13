// Saves a file the server built or streamed (Tables / Query "Export CSV", design spec §6 and §11.3;
// Backups "Download", §11.7). The .NET side passes a DotNetStreamReference because the host has no
// plugin download endpoint. contentType defaults to CSV.
export async function downloadFromStream(fileName, streamRef, contentType) {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: contentType || "text/csv;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName || "export.csv";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}
