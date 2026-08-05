import AppKit

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
// Menu-bar only: no Dock icon, no main window. LSUIElement in Info.plist does
// the same thing, but set it here too so a bare `swift run` behaves the same.
app.setActivationPolicy(.accessory)
app.run()
