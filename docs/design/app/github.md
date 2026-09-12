repo: JeffreyMcKinley/DongoDraw
branch: master

## Last sync

date: 2026-08-16T22:44:37Z

### Updated in this project

- Built a mobile prototype of the figure-drawing session app on the Nocturne design system.
- Session rules mirror `DrawingSession` — skips don't count toward the total or drawing time; the pool reshuffles each pass.
- Setup screen follows `SessionSetup` (seconds per image, image count) plus a break-between-poses control.
- Added a phone / fold-open (412 / 812) frame toggle; the unfolded viewer puts the image left, controls and progress right.

## Screen map

Corrected 2026-09-06: the 2026-08-16 map named `DongoDraw.Core/FolderImageEnumerator.cs` and
`DongoDraw.Core/Data/AppSettings.cs`, neither of which exists — the library type is
`ReferenceLibrary` and the settings type is `Settings`. Nothing checks these paths automatically, so
re-read them whenever a Core type is renamed. **This copy is ahead of the design project's; push it
back on the next sync.**

| Screen | Repo files |
| --- | --- |
| Session setup | DongoDraw.Core/SessionSetup.cs, Resources/layout/activity_main.xml, Resources/values/strings.xml |
| Pose viewer / paused | DongoDraw.Core/Session/DrawingSession.cs, DongoDraw.Core/Session/ViewerTools.cs |
| Session complete | DongoDraw.Core/Session/DrawingSession.cs (SessionSummary) |
| Images library | DongoDraw.Core/ReferenceLibrary.cs, DongoDraw.Core/LibraryReference.cs, DongoDraw.Core/LibraryLoadState.cs |
| Settings | DongoDraw.Core/Data/Settings.cs |
| Rule-of-thirds guides | DongoDraw.Core/GridContrast.cs |
