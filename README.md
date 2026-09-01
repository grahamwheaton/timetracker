# Timeline

A small, local Windows work log. When it opens, it hides itself, waits one second, then records:

- a separate, resized JPEG screenshot for each connected display;
- the active program;
- the active file or window title;
- the date and time.

## Run it

Double-click **Timeline.vbs** for a completely silent launch with no terminal window. **Start Timeline.cmd** remains available as a compatibility wrapper and immediately hands off to the same silent launcher.

The app is already built in this project. It runs through the installed Microsoft .NET desktop runtime; this avoids Windows antivirus blocking locally generated PowerShell scripts and executables.

The first automatic capture happens one second after Timeline hides. It then minimizes to the taskbar and captures activity automatically every five minutes while it is running. Use **Capture now** for an extra checkpoint at any time. You can add an optional note before a manual capture.

Timeline stores everything in the `Timeline Data` folder beside the app. It creates a readable `Summary YYYY-MM-DD.txt` containing the programs and filenames seen that day. Double-click an entry to open its screenshot.

The main window is a page for one day. Use Previous, Today, and Next to move between days. The 07:00–19:00 chart uses coloured segments to estimate how long each program and file was active from the five-minute samples. This is intended as a quick visual aid for completing a timesheet rather than minute-perfect monitoring.

If Windows has received no keyboard or mouse input for at least two minutes, the capture is shown as a grey **Idle / uncertain** segment. The foreground program is retained as context, but that segment is excluded from the daily work-summary totals. Reading, calls, video review, and waiting for renders can legitimately appear idle, so these segments remain visible for your judgement.

Screenshots are saved without alpha at a maximum dimension of 1280 pixels using medium-quality JPEG compression. Existing PNG screenshots are converted and replaced in a single startup batch.

Only today and the previous 13 calendar days are retained. Older screenshots, log entries, and daily summary files are deleted automatically when Timeline starts or records a capture.

Nothing is uploaded or sent anywhere.

## Optional startup shortcut

Press `Win + R`, enter `shell:startup`, and place a shortcut to **Start Timeline.cmd** in that folder if you want Timeline to launch when you sign into Windows.
