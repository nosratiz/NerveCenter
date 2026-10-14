// Query editor completion popup (design spec §12). The suggestions come from the server: the
// Query page's [JSInvokable] GetCompletions runs SqlCompletionEngine. This module only asks for
// them (debounced, stale answers dropped), renders the popup at the caret (mirror-div technique),
// handles the keys and inserts the accepted text. No editor library -- the <textarea> stays.
//
// Keys while the popup is open: ↑/↓ move, Enter or Tab accept, Esc closes. Ctrl/⌘+Enter is left
// alone (after closing the popup) so the page's own @onkeydown still runs the query; plain Enter is
// swallowed only when it accepts a suggestion -- the page never acts on plain Enter. ARIA: the
// textarea keeps its textbox role (aria-autocomplete, aria-controls, aria-activedescendant are
// valid on it); the popup is a listbox of options.

const DEBOUNCE_MS = 120;
const BADGES = { Keyword: "kw", Function: "fn", Table: "T", View: "V", Column: "C", Alias: "A" };
const IDENTIFIER_CHAR = /[\p{L}\p{N}_$]/u;
const controllers = new WeakMap();
let nextId = 0;

export function attach(textarea, dotnet) {
    if (textarea && !controllers.has(textarea)) {
        controllers.set(textarea, new Completion(textarea, dotnet));
    }
}

export function detach(textarea) {
    const controller = textarea && controllers.get(textarea);
    if (controller) {
        controller.dispose();
        controllers.delete(textarea);
    }
}

class Completion {
    constructor(textarea, dotnet) {
        this.textarea = textarea;
        this.dotnet = dotnet;
        this.id = `sql-complete-${++nextId}`;
        this.items = [];
        this.active = -1;
        this.from = 0;
        this.timer = 0;
        this.request = 0; // number of the latest request; any other answer is stale
        this.applying = false;

        // Lives in <body> (not inside Blazor-rendered markup, which Blazor would diff away), position: fixed.
        this.popup = document.createElement("div");
        this.popup.id = this.id;
        this.popup.className = "sql-complete";
        this.popup.setAttribute("role", "listbox");
        this.popup.setAttribute("aria-label", "SQL suggestions");
        this.popup.hidden = true;
        document.body.appendChild(this.popup);

        this.onInput = this.onInput.bind(this);
        this.onKeyDown = this.onKeyDown.bind(this);
        this.onMouseDown = this.onMouseDown.bind(this);
        this.onScroll = this.onScroll.bind(this);
        this.close = this.close.bind(this);
        textarea.addEventListener("input", this.onInput);
        textarea.addEventListener("keydown", this.onKeyDown);
        textarea.addEventListener("blur", this.close);
        this.popup.addEventListener("mousedown", this.onMouseDown);
        window.addEventListener("scroll", this.onScroll, true);
        window.addEventListener("resize", this.close);
        textarea.setAttribute("aria-autocomplete", "list");
        textarea.setAttribute("aria-controls", this.id);
    }

    get isOpen() {
        return !this.popup.hidden;
    }

    onInput(e) {
        if (this.applying || e.isComposing) {
            return;
        }

        // Typing an identifier character or "." asks; deleting refreshes an open popup; anything else closes it.
        const typed = e.inputType === "insertText" && e.data ? e.data.slice(-1) : "";
        if (typed && (typed === "." || IDENTIFIER_CHAR.test(typed))) {
            this.schedule(DEBOUNCE_MS);
        } else if (this.isOpen && e.inputType && e.inputType.startsWith("delete")) {
            this.schedule(DEBOUNCE_MS);
        } else {
            this.close();
        }
    }

    onKeyDown(e) {
        if (e.ctrlKey && !e.metaKey && !e.altKey && (e.code === "Space" || e.key === " ")) {
            e.preventDefault();
            this.schedule(0);
            return;
        }

        if (!this.isOpen) {
            return;
        }

        switch (e.key) {
            case "ArrowDown":
            case "ArrowUp":
                e.preventDefault();
                this.setActive(this.active + (e.key === "ArrowDown" ? 1 : -1));
                break;
            case "Enter":
                if (e.ctrlKey || e.metaKey) {
                    this.close(); // Ctrl/⌘+Enter runs the query (the page's handler); never accept here
                    break;
                }
                e.preventDefault();
                this.accept(this.active);
                break;
            case "Tab":
                e.preventDefault();
                this.accept(this.active);
                break;
            case "Escape":
                e.preventDefault();
                this.close();
                break;
            case "ArrowLeft":
            case "ArrowRight":
            case "Home":
            case "End":
            case "PageUp":
            case "PageDown":
                this.close();
                break;
        }
    }

    onMouseDown(e) {
        e.preventDefault(); // keep the focus (and the caret) in the textarea
        const option = e.target.closest("[role=option]");
        if (option) {
            this.accept(Number(option.dataset.index));
        }
    }

    onScroll(e) {
        if (e.target !== this.popup) {
            this.close();
        }
    }

    schedule(delay) {
        clearTimeout(this.timer);
        this.timer = setTimeout(() => this.fetch(), delay);
    }

    async fetch() {
        const text = this.textarea.value;
        const caret = this.textarea.selectionStart;
        if (this.textarea.selectionEnd !== caret) {
            this.close();
            return;
        }

        const request = ++this.request;
        let result;
        try {
            result = await this.dotnet.invokeMethodAsync("GetCompletions", text, caret);
        } catch {
            return; // circuit gone or page disposed
        }

        // Stale: a newer request went out, the popup was closed, or the text/caret moved meanwhile.
        if (request !== this.request || this.textarea.value !== text || this.textarea.selectionStart !== caret) {
            return;
        }

        if (!result || !result.items || result.items.length === 0) {
            this.close();
            return;
        }

        this.items = result.items;
        this.from = result.replaceFrom;
        this.render();
        this.position(caret);
    }

    render() {
        const options = this.items.map((item, index) => {
            const option = document.createElement("div");
            option.id = `${this.id}-${index}`;
            option.className = "sql-complete-item";
            option.setAttribute("role", "option");
            option.dataset.index = String(index);

            const kind = document.createElement("span");
            kind.className = `sql-complete-kind sql-complete-kind-${String(item.kind).toLowerCase()}`;
            kind.textContent = BADGES[item.kind] || "";
            kind.setAttribute("aria-hidden", "true");
            const label = document.createElement("span");
            label.className = "sql-complete-label";
            label.textContent = item.label;
            option.append(kind, label);
            if (item.detail) {
                const detail = document.createElement("span");
                detail.className = "sql-complete-detail";
                detail.textContent = item.detail;
                option.append(detail);
            }
            return option;
        });

        this.popup.replaceChildren(...options);
        this.popup.hidden = false;
        this.setActive(0);
    }

    setActive(index) {
        const count = this.items.length;
        this.active = ((index % count) + count) % count;
        Array.from(this.popup.children).forEach((option, i) => {
            const selected = i === this.active;
            option.classList.toggle("sql-is-active", selected);
            option.setAttribute("aria-selected", selected ? "true" : "false");
            if (selected) {
                option.scrollIntoView({ block: "nearest" });
            }
        });
        this.textarea.setAttribute("aria-activedescendant", `${this.id}-${this.active}`);
    }

    // Copies the textarea's text box into an off-screen div, so a marker span lands where the caret
    // is; below that line (or above it when there's no room), clamped to the viewport.
    position(caret) {
        const textarea = this.textarea;
        const style = getComputedStyle(textarea);
        const mirror = document.createElement("div");
        for (const name of ["boxSizing", "width", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
            "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth", "borderStyle",
            "fontFamily", "fontSize", "fontWeight", "fontStyle", "letterSpacing", "lineHeight", "textTransform",
            "wordSpacing", "textIndent", "tabSize"]) {
            mirror.style[name] = style[name];
        }
        Object.assign(mirror.style, {
            position: "absolute", visibility: "hidden", top: "0", left: "-9999px",
            whiteSpace: "pre-wrap", overflowWrap: "break-word",
            overflowY: textarea.scrollHeight > textarea.clientHeight ? "scroll" : "hidden",
        });
        mirror.textContent = textarea.value.slice(0, caret);
        const marker = document.createElement("span");
        marker.textContent = textarea.value.slice(caret) || ".";
        mirror.appendChild(marker);
        document.body.appendChild(mirror);
        const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.4;
        const caretTop = marker.offsetTop - textarea.scrollTop;
        const caretLeft = marker.offsetLeft - textarea.scrollLeft;
        mirror.remove();

        const box = textarea.getBoundingClientRect();
        const popup = this.popup.getBoundingClientRect();
        const lineTop = box.top + Math.max(0, Math.min(caretTop, box.height - lineHeight));
        let top = lineTop + lineHeight;
        if (top + popup.height > window.innerHeight - 8 && lineTop - popup.height > 8) {
            top = lineTop - popup.height;
        }
        const left = Math.max(8, Math.min(box.left + Math.max(0, caretLeft), window.innerWidth - popup.width - 8));
        this.popup.style.top = `${Math.round(top)}px`;
        this.popup.style.left = `${Math.round(left)}px`;
    }

    accept(index) {
        const item = this.items[index];
        const textarea = this.textarea;
        const caret = textarea.selectionStart;
        const from = this.from;
        this.close();
        if (!item || caret < from) {
            return;
        }

        // Replace the identifier now at `from` -- the user may have typed more since the answer.
        let to = from;
        while (to < textarea.value.length && IDENTIFIER_CHAR.test(textarea.value[to])) {
            to++;
        }
        textarea.setRangeText(item.insertText, from, Math.max(to, from), "end");
        // Blazor's @oninput reads the new value from this event.
        this.applying = true;
        try {
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
        } finally {
            this.applying = false;
        }
    }

    close() {
        clearTimeout(this.timer);
        this.request++; // drop any answer still in flight
        this.items = [];
        this.active = -1;
        this.popup.hidden = true;
        this.popup.replaceChildren();
        this.textarea.removeAttribute("aria-activedescendant");
    }

    dispose() {
        this.close();
        this.textarea.removeEventListener("input", this.onInput);
        this.textarea.removeEventListener("keydown", this.onKeyDown);
        this.textarea.removeEventListener("blur", this.close);
        window.removeEventListener("scroll", this.onScroll, true);
        window.removeEventListener("resize", this.close);
        for (const name of ["aria-autocomplete", "aria-controls"]) {
            this.textarea.removeAttribute(name);
        }
        this.popup.remove();
    }
}
