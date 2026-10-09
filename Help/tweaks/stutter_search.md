# Windows Search is indexing

## Why it matters

The indexer reads files and writes its catalogue to the drive while it works. After a big install, a Windows update or a move of your Documents folder it can run for a long while, and a game loading levels at the same time stutters as the two share the drive.

## What to do

- **Wait**: it settles once the catalogue is up to date, usually within an hour of whatever changed.
- **Pause it for a day**: Windows Settings > Privacy & security > Searching Windows > Advanced indexing options > Pause. It restarts on its own after 24 hours.
- **Index less**: in the same place, remove folders you never search (a games drive, for instance), so there is less to keep up to date.

## Details

Stutter Check counts the indexer as busy when SearchIndexer uses more than about 3 % of the CPU over a short sample. Idle or not running counts as fine. TrayTrigger never stops the Windows Search service.
