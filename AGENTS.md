# QuickCapture

- Preserve Win+Shift+S for images, Win+Shift+R for recording, and Win+Shift+T for text. Print Screen is opt-in. Pass unrelated shortcuts through.
- Keep the keyboard hook on a dedicated message loop. While an overlay is visible but cannot get focus, route only its capture commands to it. Never do screen capture, disk I/O, or clipboard I/O inside the hook callback.
- Keep region coordinates in physical desktop pixels. Capture the screen before showing the selection overlay.
- Ctrl+C in selection review or quick markup must explicitly copy and close the overlay, independently of the automatic copy and PNG settings. Tray-only startup must leave the editor closed, including duplicate launches.
- By default, save each completed capture as PNG before attempting clipboard publication. Publish materialized formats and keep clipboard contention off the UI thread.
- Save capture and recording checkbox changes immediately in the shared settings. New overlays and quick markup must use the last saved values, including after cancellation.
- Keep visual and functional comparisons anchored to the installed Microsoft.ScreenSketch and official Microsoft documentation. Report approximate and unverified behavior distinctly.
- Run `restore-dependencies.ps1` before `build.ps1`. Pin package versions and hashes. Keep downloads and build outputs outside Git and include upstream notices with the portable build.
- Verify without overwriting the user's existing clipboard or sending input into an active desktop.
