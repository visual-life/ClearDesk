# 2.2.2 progress (written 2026-10-27 13:05, Asia/Shanghai)
Done and tested (all 5 suites passed; delivered C:\work\ClearDesk-2.2.2, .zip, .zip.sha256):
1. Organizer flash on activation (live-blur window z-order desync; WM_WINDOWPOSCHANGED reorder hook) + Dock jitter (rebuild debounce/signature skip, auto-hide show delay).
2. Position drift after restart (3 causes: new widget not saved immediately, system-driven moves saved, DIP/pixel reachability check) -> PositionGuard.
3. Multi-instance: album, todo, countdown/pomodoro, schedule, launcher (ids kind, kind~N); clipboard/netspeed stay single; first instance keeps old data.
4. Custom background for all widgets (choose/paste/drop, fit, opacity, dim, remove).
5. Frameless / transparent option (clock + all widgets except dock/launchpad; notes without frameless).
6. Edge metrics scaling (MaxScale 4, padding 20-40, gaps up to 3x), pomodoro tab/mode text sizes. Before/after renders in docs/images/2.2.2.
Not done / unverified: Dock jump and system-nudge cause of drift were NOT reproduced live on the user's machine (only by unit simulation).