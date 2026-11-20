# UI test run

Commit 5be49f955a0990853693ade0ea1196344f6c0cb3 (trust-0.9), 2026-11-20 00:14 UTC, run [44642298482](https://github.com/BrainCluster/vibe-salamander/actions/runs/44642298482)

| Test | Result | Seconds |
|---|---|---:|
| Duplicate a file in place | Passed | 3.6 |
| The arrow at the end of the drive bar opens and closes the row of tabs | Passed | 7.8 |
| Markdown notebook and web page rendered | Passed | 10.4 |
| A folder sent to the running program opens in a new tab | Passed | 3.1 |
| Find with results | Passed | 11.5 |
| Views | Passed | 7.9 |
| Croatian | Passed | 3.9 |
| Copy with F5 | Passed | 4.1 |
| Compare and synchronize | Passed | 4.0 |
| The directory line can be hidden | Passed | 5.6 |
| Configuration pages | Passed | 10.9 |
| Into a zip and copy a file out | Passed | 6.4 |
| A repository folder has git in its right click menu | Passed | 19.6 |
| A folder copied onto itself gets a copy name | Passed | 5.8 |
| Picture viewer | Passed | 6.6 |
| Hot path there and back | Failed | 61.7 |
| Pack then unpack | Passed | 9.0 |
| Folders from the command line | Passed | 2.4 |
| Find window | Passed | 2.8 |
| Drag a file to the other panel | NotExecuted | 0.0 |
| Example plugin | Passed | 8.8 |
| The panel shows the branch and each files git state | Passed | 4.1 |
| Rename and make a folder inside a zip | Passed | 6.3 |
| Media Info shows a sound files details | Passed | 5.5 |
| Check for updates answers | Passed | 3.5 |
| Convert gives a file Windows line endings | Passed | 3.7 |
| Create a directory rename and delete | Passed | 6.6 |
| A hot path with a shortcut of its own | Passed | 3.5 |
| Dates as iso | Passed | 2.5 |
| A big log in text and hex with find | Passed | 6.1 |
| Pdf and sound | Passed | 8.1 |
| Move with F6 | Passed | 4.1 |
| A plugin viewer shows a csv file | Passed | 4.1 |
| Swap panels | Passed | 2.6 |
| Color schemes density and a small window | Passed | 9.6 |
| Disk map plugin | Passed | 4.1 |
| First steps switch to total commander keys | Passed | 4.4 |
| Outside a repository there is no git column | Passed | 3.1 |
| Hero pictures | Passed | 8.2 |
| A full row of tabs scrolls with arrows at both ends | Passed | 10.9 |
| Sort by size and back by name | Passed | 3.3 |
| Every view on pictures | Passed | 5.0 |
| Marking and the context menu | Passed | 4.5 |
| Find text rows and show in panel | Passed | 3.9 |
| A plugin has a settings page | Passed | 4.5 |
| Always showing the row has no arrow on the drive bar | Passed | 2.7 |
| Edit with lists the editors | Passed | 5.2 |
| Multi rename with a counter | Passed | 4.6 |
| Change attributes and case dialogs | Passed | 4.4 |
| A tab has a close button under the pointer | NotExecuted | 0.0 |
| Compare directories marks the differences | Passed | 3.0 |
| A file only in the cloud has a cloud over its icon | Passed | 3.3 |
| Startup time and memory are measured | Passed | 20.4 |
| Changes are staged compared and committed | Passed | 15.3 |
| F3 on a sound file shows its media information | Passed | 4.3 |
| Checksum plugin | Passed | 5.0 |
| Compare two files | Passed | 7.0 |
| Plugin windows | Passed | 13.5 |
| This PC and the dark theme | Passed | 2.4 |
| Hidden files on and off | Passed | 3.2 |
| Editor sorts the keys of a json | Passed | 7.7 |
| Media Info is in the Plugins menu and not on text files | Passed | 7.5 |
| Menus | Passed | 8.2 |
| Archive dialogs | Passed | 9.8 |
| Tabs are dragged into a new order and to the other panel | NotExecuted | 0.0 |
| An opened disk image opens in a new tab of the other panel and ejects from it | Failed | 65.1 |
| User menu and about | Passed | 7.0 |
| Configuration window | Passed | 3.5 |
| Error log says there are none | Passed | 3.0 |
| Copy onto an existing file asks and overwrites | Passed | 4.1 |
| A speed limit and the graph | Passed | 8.7 |
| Main window on the dummy files | Passed | 2.7 |
| Json xml and yaml as trees | Passed | 10.1 |
| Edit and save code | Passed | 8.2 |
| Flat view and quick view | Passed | 4.8 |
| Ctrl U swaps the panels with their tabs | Passed | 3.1 |
| Pictures go through the folder | Passed | 5.1 |
| History blame and an old version copied out | Passed | 11.0 |
| Modern views open picture folders as thumbnails | Passed | 2.5 |
| Markdown with its live preview | Passed | 10.0 |
| Tabs open switch keep their marks lock and close | Passed | 15.1 |
| Hot path list and names into the command line | Passed | 5.7 |
| Find duplicates and select the copies | Passed | 4.0 |
| Json problems show while typing | Passed | 9.8 |
| Quick search and the transfer panel | Passed | 7.4 |
| Mark by mask and copy the marked files | Passed | 4.1 |
| Filter shows only matching files | Passed | 3.8 |
| Directory sizes | Passed | 2.8 |
| Push and fetch run as jobs in the queue | Passed | 11.1 |
| Icon packs | Passed | 4.7 |
| Into a folder and back | Passed | 4.5 |
| Viewer and editor | Passed | 27.8 |
| A folder of 5000 files | Passed | 5.2 |
| Picture viewer fit width mirror and full screen | Passed | 6.2 |
| Double click a folder and right click a file | Passed | 6.1 |

## Failures

### Hot path there and back

```
System.TimeoutException : Waited 60 s for: the app to be ready after its start (it says: "")
```

### Drag a file to the other panel

```
WinUI doesn't start drags from simulated mouse input on the test machine
```

### A tab has a close button under the pointer

```
Simulated pointer moves don't reach WinUI on GitHub's runners: run it locally.
```

### Tabs are dragged into a new order and to the other panel

```
Simulated pointer moves don't reach WinUI on GitHub's runners: run it locally.
```

### An opened disk image opens in a new tab of the other panel and ejects from it

```
System.TimeoutException : Waited 60 s for: a new tab of the mounted drive, with its label, after Target in the right panel
```

## Where the time went

| Where the time went | Seconds | Times |
|---|---:|---:|
| Fixed pauses | 218.1 | 847 |
| Starting the app (until it is ready) | 148.5 | 96 |
| Waiting for a state | 117.6 | 1,051 |
| Closing the app | 24.6 | 96 |
| Making the dummy files | 20.0 | 93 |
| Waiting while the app is busy (after keys and clicks) | 19.6 | 488 |
| Screenshots | 11.3 | 148 |
| **The whole run so far** | **712.6** | |

## Measurements

| Measured | Value |
|---|---:|
| Start until both panels are listed (to about 0.2 s) | 1,435 ms |
| Memory after the start | 80 MB |
| Start with a folder of 20,000 files in the left panel | 1,582 ms |
| End key: the caret on the last of the 20,000 files (to about 0.2 s) | 254 ms |
| Memory with the folder of 20,000 files | 128 MB |

## Screenshots

### 01 main window

![01 main window](<01 main window.png>)

### 02 view brief

![02 view brief](<02 view brief.png>)

### 03 view detailed

![03 view detailed](<03 view detailed.png>)

### 04 view icons

![04 view icons](<04 view icons.png>)

### 05 view thumbnails

![05 view thumbnails](<05 view thumbnails.png>)

### 06 view tiles

![06 view tiles](<06 view tiles.png>)

### 10 copy dialog

![10 copy dialog](<10 copy dialog.png>)

### 11 after copy

![11 after copy](<11 after copy.png>)

### 12 a folder copied onto itself

![12 a folder copied onto itself](<12 a folder copied onto itself.png>)

### 12 create directory

![12 create directory](<12 create directory.png>)

### 13 rename in place

![13 rename in place](<13 rename in place.png>)

### 14 after delete

![14 after delete](<14 after delete.png>)

### 20 marked items

![20 marked items](<20 marked items.png>)

### 21 context menu

![21 context menu](<21 context menu.png>)

### 22 inside a folder

![22 inside a folder](<22 inside a folder.png>)

### 30 find window

![30 find window](<30 find window.png>)

### 31 configuration

![31 configuration](<31 configuration.png>)

### 40 this pc dark

![40 this pc dark](<40 this pc dark.png>)

### 41 modern views

![41 modern views](<41 modern views.png>)

### 42 croatian copy dialog

![42 croatian copy dialog](<42 croatian copy dialog.png>)

### 50 5000 files

![50 5000 files](<50 5000 files.png>)

### 60 move dialog

![60 move dialog](<60 move dialog.png>)

### 61 overwrite question

![61 overwrite question](<61 overwrite question.png>)

### 62 select by mask

![62 select by mask](<62 select by mask.png>)

### 63 marked by mask

![63 marked by mask](<63 marked by mask.png>)

### 64 cloud mark

![64 cloud mark](<64 cloud mark.png>)

### 64 sorted by size

![64 sorted by size](<64 sorted by size.png>)

### 65 hidden files off

![65 hidden files off](<65 hidden files off.png>)

### 65 tabs

![65 tabs](<65 tabs.png>)

### 66 filter

![66 filter](<66 filter.png>)

### 66 tab row opened

![66 tab row opened](<66 tab row opened.png>)

### 67 directory sizes

![67 directory sizes](<67 directory sizes.png>)

### 67 full row of tabs

![67 full row of tabs](<67 full row of tabs.png>)

### 68 compare directories

![68 compare directories](<68 compare directories.png>)

### 68 tab count badge

![68 tab count badge](<68 tab count badge.png>)

### 69 compared

![69 compared](<69 compared.png>)

### 70 inside a zip

![70 inside a zip](<70 inside a zip.png>)

### 71 pack dialog

![71 pack dialog](<71 pack dialog.png>)

### 72 unpack dialog

![72 unpack dialog](<72 unpack dialog.png>)

### 74 right-click menu

![74 right-click menu](<74 right-click menu.png>)

### 75 change attributes

![75 change attributes](<75 change attributes.png>)

### 76 change case

![76 change case](<76 change case.png>)

### 77 disk map

![77 disk map](<77 disk map.png>)

### 78 checksums

![78 checksums](<78 checksums.png>)

### 79 no errors logged

![79 no errors logged](<79 no errors logged.png>)

### 80 convert

![80 convert](<80 convert.png>)

### 80 norton commander

![80 norton commander](<80 norton commander.png>)

### 81 dracula

![81 dracula](<81 dracula.png>)

### 81 edit with

![81 edit with](<81 edit with.png>)

### 82 explorer

![82 explorer](<82 explorer.png>)

### 82 no directory line

![82 no directory line](<82 no directory line.png>)

### 83 comfortable small window

![83 comfortable small window](<83 comfortable small window.png>)

### 85 media info in the right-click menu

![85 media info in the right-click menu](<85 media info in the right-click menu.png>)

### 85 plugin viewer

![85 plugin viewer](<85 plugin viewer.png>)

### 86 media info

![86 media info](<86 media info.png>)

### 86 plugin settings

![86 plugin settings](<86 plugin settings.png>)

### 87 media info in the viewer

![87 media info in the viewer](<87 media info in the viewer.png>)

### 88 git in the panel

![88 git in the panel](<88 git in the panel.png>)

### 89 git changes

![89 git changes](<89 git changes.png>)

### 90 a folder of 20,000 files

![90 a folder of 20,000 files](<90 a folder of 20,000 files.png>)

### 90 git compare

![90 git compare](<90 git compare.png>)

### 91 git blame

![91 git blame](<91 git blame.png>)

### 92 git history

![92 git history](<92 git history.png>)

### 93 git browse this version

![93 git browse this version](<93 git browse this version.png>)

### 94 git push in the queue

![94 git push in the queue](<94 git push in the queue.png>)

### 95 git pulled

![95 git pulled](<95 git pulled.png>)

### 96 git in a repository folder's menu

![96 git in a repository folder's menu](<96 git in a repository folder's menu.png>)

### 97 git in the repository folder's own menu

![97 git in the repository folder's own menu](<97 git in the repository folder's own menu.png>)

### doc about

![doc about](<doc about.png>)

### doc configuration about

![doc configuration about](<doc configuration about.png>)

### doc configuration appearance

![doc configuration appearance](<doc configuration appearance.png>)

### doc configuration colors

![doc configuration colors](<doc configuration colors.png>)

### doc configuration general

![doc configuration general](<doc configuration general.png>)

### doc configuration keyboard

![doc configuration keyboard](<doc configuration keyboard.png>)

### doc configuration panels

![doc configuration panels](<doc configuration panels.png>)

### doc configuration toolbar

![doc configuration toolbar](<doc configuration toolbar.png>)

### doc configuration views

![doc configuration views](<doc configuration views.png>)

### doc disk map blocks

![doc disk map blocks](<doc disk map blocks.png>)

### doc disk map classic

![doc disk map classic](<doc disk map classic.png>)

### doc disk map columns

![doc disk map columns](<doc disk map columns.png>)

### doc disk map sunburst

![doc disk map sunburst](<doc disk map sunburst.png>)

### doc editor json problem

![doc editor json problem](<doc editor json problem.png>)

### doc editor markdown

![doc editor markdown](<doc editor markdown.png>)

### doc find results

![doc find results](<doc find results.png>)

### doc ftp connect

![doc ftp connect](<doc ftp connect.png>)

### doc hello largest files

![doc hello largest files](<doc hello largest files.png>)

### doc hello menu

![doc hello menu](<doc hello menu.png>)

### doc hero dark

![doc hero dark](<doc hero dark.png>)

### doc icons vibe salamander in color

![doc icons vibe salamander in color](<doc icons vibe salamander in color.png>)

### doc menu commands

![doc menu commands](<doc menu commands.png>)

### doc menu files

![doc menu files](<doc menu files.png>)

### doc menu options

![doc menu options](<doc menu options.png>)

### doc menu plugins

![doc menu plugins](<doc menu plugins.png>)

### doc pack advanced

![doc pack advanced](<doc pack advanced.png>)

### doc pack

![doc pack](<doc pack.png>)

### doc plugins manager

![doc plugins manager](<doc plugins manager.png>)

### doc quick search

![doc quick search](<doc quick search.png>)

### doc scheme tokyo night

![doc scheme tokyo night](<doc scheme tokyo night.png>)

### doc test archive

![doc test archive](<doc test archive.png>)

### doc transfer panel

![doc transfer panel](<doc transfer panel.png>)

### doc user menu

![doc user menu](<doc user menu.png>)

### doc view brief

![doc view brief](<doc view brief.png>)

### doc view detailed

![doc view detailed](<doc view detailed.png>)

### doc view icons

![doc view icons](<doc view icons.png>)

### doc view thumbnails

![doc view thumbnails](<doc view thumbnails.png>)

### doc view tiles

![doc view tiles](<doc view tiles.png>)

### doc viewer big file

![doc viewer big file](<doc viewer big file.png>)

### doc viewer markdown

![doc viewer markdown](<doc viewer markdown.png>)

### doc viewer picture

![doc viewer picture](<doc viewer picture.png>)

### doc viewer tree

![doc viewer tree](<doc viewer tree.png>)

### FAILED - a new tab of the mounted drive, with its label, after Target in the right panel

![FAILED - a new tab of the mounted drive, with its label, after Target in the right panel](<FAILED - a new tab of the mounted drive, with its label, after Target in the right panel.png>)

### FAILED - the app to be ready after its start (it says )

![FAILED - the app to be ready after its start (it says )](<FAILED - the app to be ready after its start (it says ).png>)

### g01 duplicate named in place

![g01 duplicate named in place](<g01 duplicate named in place.png>)

### g02 multi-rename

![g02 multi-rename](<g02 multi-rename.png>)

### g03 flat view

![g03 flat view](<g03 flat view.png>)

### g04 quick view

![g04 quick view](<g04 quick view.png>)

### r01 folders from the command line

![r01 folders from the command line](<r01 folders from the command line.png>)

### r02 hot path list

![r02 hot path list](<r02 hot path list.png>)

### r02b drive menu

![r02b drive menu](<r02b drive menu.png>)

### r02c more hot paths in the menu

![r02c more hot paths in the menu](<r02c more hot paths in the menu.png>)

### r03 first steps

![r03 first steps](<r03 first steps.png>)

### r04 picture full screen mirrored

![r04 picture full screen mirrored](<r04 picture full screen mirrored.png>)

### r05 editor keys sorted

![r05 editor keys sorted](<r05 editor keys sorted.png>)

### r06 check for updates

![r06 check for updates](<r06 check for updates.png>)

### s01 find with text rows

![s01 find with text rows](<s01 find with text rows.png>)

### s02 results in the panel

![s02 results in the panel](<s02 results in the panel.png>)

### s03 duplicates

![s03 duplicates](<s03 duplicates.png>)

### s04 compare list

![s04 compare list](<s04 compare list.png>)

### s05 after synchronizing

![s05 after synchronizing](<s05 after synchronizing.png>)

### s06 transfer graph and limit

![s06 transfer graph and limit](<s06 transfer graph and limit.png>)

### s07 compare files

![s07 compare files](<s07 compare files.png>)

### v01 viewer text

![v01 viewer text](<v01 viewer text.png>)

### v02 viewer find

![v02 viewer find](<v02 viewer find.png>)

### v03 viewer hex

![v03 viewer hex](<v03 viewer hex.png>)

### v04 viewer json tree

![v04 viewer json tree](<v04 viewer json tree.png>)

### v05 viewer xml tree

![v05 viewer xml tree](<v05 viewer xml tree.png>)

### v06 viewer yaml tree

![v06 viewer yaml tree](<v06 viewer yaml tree.png>)

### v07 viewer yaml text

![v07 viewer yaml text](<v07 viewer yaml text.png>)

### v08 viewer markdown

![v08 viewer markdown](<v08 viewer markdown.png>)

### v09 viewer notebook

![v09 viewer notebook](<v09 viewer notebook.png>)

### v10 viewer web page

![v10 viewer web page](<v10 viewer web page.png>)

### v11 viewer picture

![v11 viewer picture](<v11 viewer picture.png>)

### v12 viewer next picture rotated

![v12 viewer next picture rotated](<v12 viewer next picture rotated.png>)

### v13 viewer pdf

![v13 viewer pdf](<v13 viewer pdf.png>)

### v14 viewer sound

![v14 viewer sound](<v14 viewer sound.png>)

### v15 editor code

![v15 editor code](<v15 editor code.png>)

### v16 editor json problem

![v16 editor json problem](<v16 editor json problem.png>)

### v17 editor markdown preview

![v17 editor markdown preview](<v17 editor markdown preview.png>)

