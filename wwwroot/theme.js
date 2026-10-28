window.lessonTheme = {
    initialize() {
        let theme = "light";
        try { theme = localStorage.getItem("c13-theme") || "light"; } catch { }
        document.documentElement.dataset.theme = theme === "dark" ? "dark" : "light";
        return theme === "dark";
    },
    toggle() {
        const dark = document.documentElement.dataset.theme !== "dark";
        document.documentElement.dataset.theme = dark ? "dark" : "light";
        try { localStorage.setItem("c13-theme", dark ? "dark" : "light"); } catch { }
        return dark;
    }
};
window.lessonTheme.initialize();
