// Kubernetes plugin browser helpers (ES module, imported lazily by pages and components).

// Copy button on YAML views and log lines.
export async function copyText(text) {
    await navigator.clipboard.writeText(text ?? "");
}

// Saves a file the server built ("Download logs"). The .NET side passes a DotNetStreamReference because
// the host has no plugin download endpoint.
export async function downloadFile(name, streamRef) {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: "application/octet-stream" }));
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = name || "download.txt";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}

// Auto-refresh pauses while the tab is hidden: the page exposes [JSInvokable] SetHidden(bool).
// Reports the current state once, then on every change. Call dispose() from the page's DisposeAsync.
export function watchVisibility(dotnetRef) {
    const handler = () => dotnetRef.invokeMethodAsync("SetHidden", document.hidden);
    document.addEventListener("visibilitychange", handler);
    handler();
    return {
        dispose() {
            document.removeEventListener("visibilitychange", handler);
        },
    };
}

// Log viewer "follow": keeps the newest line in view.
export function scrollToBottom(id) {
    const element = document.getElementById(id);
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}

// Log viewer search: brings the current match into view.
export function scrollIntoView(id) {
    const element = document.getElementById(id);
    if (element) {
        element.scrollIntoView({ block: "center", inline: "nearest" });
    }
}
