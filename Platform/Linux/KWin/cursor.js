// The application loads this script while desktop input is enabled.
// C# replaces the destination with its unique D-Bus name.
// KWin provides desktop logical pixels, including output scaling and negative offsets.
function reportCursor() {
    var cursor = workspace.cursorPos;
    var monitors = [];
    var screens = workspace.screens;
    for (var index = 0; index < screens.length; index++) {
        var screen = screens[index];
        var geometry = screen.geometry;
        monitors.push({ name: screen.name, x: geometry.x, y: geometry.y, width: geometry.width, height: geometry.height, scale: screen.devicePixelRatio });
    }
    var active = workspace.activeWindow;
    var activeInfo = null;
    if (active) {
        var bounds = active.clientGeometry;
        activeInfo = { processId: active.pid, caption: active.caption, clientBounds: { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height } };
    }
    callDBus("__DESTINATION__", "/org/autoclicker/Cursor", "org.autoclicker.Cursor", "Report", JSON.stringify({ cursor: { x: cursor.x, y: cursor.y }, monitors: monitors, activeWindow: activeInfo }));
}
workspace.cursorPosChanged.connect(reportCursor);
workspace.windowActivated.connect(reportCursor);
workspace.screensChanged.connect(reportCursor);
workspace.virtualScreenGeometryChanged.connect(reportCursor);
// Scale/geometry changes can occur without adding or removing an output.
var watchedScreens = [];
function watchScreens() {
    var screens = workspace.screens;
    for (var index = 0; index < screens.length; index++) {
        var screen = screens[index];
        if (watchedScreens.indexOf(screen) >= 0) { continue; }
        watchedScreens.push(screen);
        screen.geometryChanged.connect(reportCursor);
        screen.scaleChanged.connect(reportCursor);
    }
}
workspace.screensChanged.connect(watchScreens);
watchScreens();
reportCursor();
