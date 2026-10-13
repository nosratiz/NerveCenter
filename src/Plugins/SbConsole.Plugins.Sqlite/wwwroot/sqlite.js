// Saves a file the server built (Tables page "Export CSV", design spec §6). The .NET side passes a
// DotNetStreamReference because the host has no plugin download endpoint.
export async function downloadFromStream(fileName, streamRef) {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: "text/csv;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName || "export.csv";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
}
