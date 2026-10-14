// Saves a file the server built (Browse / Query "Export CSV"). The .NET side passes a DotNetStreamReference because
// the host has no plugin download endpoint. contentType defaults to CSV.
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

// Ctrl/Cmd+Enter inside the element runs the editor's query (Query page). One handler per element.
const handlers = new WeakMap();

export function registerRunShortcut(element, dotnetRef) {
    if (!element || handlers.has(element)) {
        return;
    }

    const handler = (event) => {
        if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
            event.preventDefault();
            dotnetRef.invokeMethodAsync("RunFromShortcut");
        }
    };
    element.addEventListener("keydown", handler);
    handlers.set(element, handler);
}

export function unregisterRunShortcut(element) {
    const handler = element && handlers.get(element);
    if (handler) {
        element.removeEventListener("keydown", handler);
        handlers.delete(element);
    }
}
