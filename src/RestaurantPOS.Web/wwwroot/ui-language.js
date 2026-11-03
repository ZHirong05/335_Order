window.posLanguage = {
    async set(form) {
        const data = new FormData(form);
        const response = await fetch(form.action, {
            method: "POST",
            credentials: "same-origin",
            headers: { "X-InPlace-Language": "true" },
            body: data
        });
        if (!response.ok) throw new Error("Language preference could not be saved");
        document.documentElement.lang = data.get("language") === "zh" ? "zh-Hans" : "en";
    }
};
