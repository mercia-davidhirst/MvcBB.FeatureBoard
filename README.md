# MvcBB Feature Board

A feature request board mod for MvcBB, built against MvcBB's mod framework
(`MvcBB.ModKit`). Members suggest features, like and discuss each other's
ideas, and Administrators move them across a kanban-style board as they're
planned, built and released (or discarded). Lives in its own solution,
separate from MvcBB's core codebase.

Requires MvcBB.ModKit **1.1.0** or later (core's view slots).

## Architecture

- **MvcBB.FeatureBoard.Api** - the API-side half: data access, permission
  rules, word filtering, and the HTTP endpoints MvcBB.App's side calls.
  Implements `IApiMod`, so MvcBB.API discovers it, registers its endpoints,
  and installs its database schema automatically once the built assembly is
  placed in MvcBB.API's `Mods/` folder.
- **MvcBB.FeatureBoard.App** - the board, request pages and forms, as a Razor
  Class Library. Implements `IAppMod`, so MvcBB.App discovers it, registers
  its controller and precompiled views, adds its "Feature Board" nav link,
  and adds its stylesheet to core pages once its built assembly is placed in
  MvcBB.App's `Mods/` folder.
- **MvcBB.FeatureBoard.Tests** - xUnit tests for the API's rules (statuses,
  who can change them, likes, comments and additional information only while
  a request is open, deleting, ordering, word filter) against the in-memory
  provider, plus the App's helpers.

Both mod projects depend only on `MvcBB.ModKit` and `MvcBB.Shared` (plus, for
the API, MvcBB's own connection-factory projects for direct database access)
- never on MvcBB.App's or MvcBB.API's internal types. Core features are
reused through `MvcBB.Shared`:
- **BBCode and smilies**: descriptions, additional information, comments and
  status comments are rendered with core's `IBBCodeService`, which
  HTML-encodes everything before applying tags, with core's active smilies.
  The request form uses core's own BBCode toolbar
  (`Views/Shared/_BBCodeToolbar.cshtml`).
- **Word filter**: every piece of member-written text (titles, descriptions,
  additional information, comments, status comments) goes through core's
  `IWordFilterRepository` and `WordFilterApplier` before it's saved, exactly
  as core filters posts, so the stored text is already clean.

## Functionality

### The board
- The **Feature Board** page (nav link) shows five columns, left to right:
  **Discarded**, **Requested**, **To-do**, **In progress** and **Released**.
  Each shows how many requests it holds and its first 25 cards; "View all"
  opens the whole column, 25 per page.
- **Requested** is sorted by likes (most first), then newest. Every other
  column is sorted by when the request moved there, latest first.
- Each card shows the title, a short plain-text teaser, the author, and its
  like and comment counts. Cards in Requested have a like button (except on
  the member's own requests).
- An **All requests / My requests** filter above the board switches it to
  just the member's own requests (every one of them, in the same columns).
- On narrow screens the board scrolls sideways.

### Requests
- **Request a feature** from the board: a title and a BBCode description. A
  new request goes into **Requested**.
- While a request is in Requested:
  - any other member can **like** it (never their own) and unlike it again;
  - any member can **comment** on it (BBCode works in comments too);
  - its author can add **additional information**, shown under the
    description with its date.
- Once a request moves to any other column, its likes, comments and
  additional information are **frozen** as they were. If an Administrator
  moves it back to Requested, they open again.

### Status changes
- Only **Administrators** move a request between columns, from the "Change
  status" panel on the request's page. Any column can move to any other.
- Every move needs a **comment** explaining it, except discarding, where the
  reason is optional.
- Each move is kept in the request's **status history** (from and to, who,
  when, and the comment), shown to every member on the request's page.

### Who can do what

| | Author | Other members | Administrators |
| --- | --- | --- | --- |
| See the board and every request | Yes | Yes | Yes |
| Make a request | Yes | Yes | Yes |
| Like (while Requested) | No (own request) | Yes | Yes (not their own) |
| Comment (while Requested) | Yes | Yes | Yes |
| Add information (while Requested) | Yes | No | No (unless they're the author) |
| Change status | No | No | Yes |
| Delete the request | While Requested | No | Any time |
| Delete a comment | Own, while Requested | Own, while Requested | Any, any time |
| Delete additional information | While Requested | No | Any time |

Deleting a request removes its additional information, likes, comments and
status history.

### On core pages (view slots)
- **Stylesheet** (`layout.head`): the mod's `featureboard.css`, embedded in the
  DLL and served by MvcBB.App at `/_mods/featureboard/css/featureboard.css`.
- **Newest requests card** (`home.right`): a "Feature Board" card on the home
  page listing the 5 most recently made requests, newest first, from any
  column except Discarded, each with its author and current column, linking
  to the request.
  Every signed-in member sees it, and the same list for everyone, since every
  member can see every request. The list is cached for a minute, so a new
  request can take up to a minute to appear. If the API can't be reached the
  card is left out rather than breaking the page.
- **New request alert, for Administrators** (`layout.navbar` and
  `layout.toasts`): works like core's open-reports alert. A kanban icon in
  the navbar carries a badge with the number of requests waiting in
  Requested, and links to that column. `featureboard-alert.js` (embedded and
  served like the stylesheet) polls `/FeatureBoard/RequestedCount` every 30
  seconds, updates the badge, and shows a toast beside core's report toast
  when the count goes up. Needs a core with those two slots; an older core
  logs and ignores them, and the rest of the mod works as before.

None of it renders for guests.

## Data providers

Supports all three of MvcBB's data providers - SQL Server, PostgreSQL, and
the in-memory provider used for local development - following whichever
`DataProvider` MvcBB.API itself is configured with. Schema for SQL Server and
PostgreSQL is created automatically (idempotently, safe to redeploy) the first
time the mod runs against a database that doesn't have it yet, and upgraded
whenever the mod's `Version` changes.

| SQL Server | PostgreSQL | Holds |
| --- | --- | --- |
| `dbo.Mod_FeatureBoard_Requests` | `public.mod_featureboard_requests` | Id, AuthorUserId, Title, Description, Status, CreatedAt, StatusChangedAt |
| `dbo.Mod_FeatureBoard_Additions` | `public.mod_featureboard_additions` | Id, RequestId, Content, CreatedAt |
| `dbo.Mod_FeatureBoard_Likes` | `public.mod_featureboard_likes` | RequestId, UserId (together the primary key, so one like per member), CreatedAt |
| `dbo.Mod_FeatureBoard_Comments` | `public.mod_featureboard_comments` | Id, RequestId, UserId, Content, CreatedAt |
| `dbo.Mod_FeatureBoard_StatusChanges` | `public.mod_featureboard_status_changes` | Id, RequestId, FromStatus, ToStatus, ChangedByUserId, Comment, CreatedAt |

Status is stored as a small number: 0 Discarded, 1 Requested, 2 To-do,
3 In progress, 4 Released (checked by a constraint). Everything on a request
cascades from it, so a delete is one statement. Author, commenter and
Administrator columns reference core's `Users` table without a cascade: core
anonymizes accounts rather than deleting them, so a member's requests and
comments stay in place. Like and comment counts are computed on read rather
than stored.

## Security

- **Login everywhere.** Every App page and every API endpoint requires a
  signed-in member. The App calls the API with the member's own token, never
  a shared or service token.
- **Rules enforced by the API.** Every permission in the table above is
  checked in the API (`FeatureBoardAccess`), not just by hiding buttons.
  Status changes from anyone but an Administrator are refused (403), as is a
  move without a comment (except discarding) or without a target column.
- **Open only while Requested, even under races.** Likes, comments and
  additional information on a request that isn't in Requested are refused
  (409). The database write itself re-checks the status while holding a lock
  on the request's row (`UPDLOCK` on SQL Server, `FOR UPDATE` on PostgreSQL),
  and a status change only applies if the request is still where the
  Administrator saw it, so nothing slips in while a request is being moved.
- **CSRF and XSS.** Every form post carries an antiforgery token. BBCode goes
  through core's encoding renderer; everything else is encoded by Razor.
- **Rate limits.** New requests (5 per 10 minutes per member) and comments or
  additional information (30 per 10 minutes) are counted in memory, so they
  reset when MvcBB.App restarts.

## Installing

1. Build the solution (`dotnet build MvcBB.FeatureBoard.slnx`), and optionally
   run the tests (`dotnet test MvcBB.FeatureBoard.slnx`).
2. Copy `MvcBB.FeatureBoard.Api.dll` into MvcBB.API's own `Mods/` folder (next
   to its other build output), and `MvcBB.FeatureBoard.App.dll` into
   MvcBB.App's `Mods/` folder.
3. Restart both processes, so each discovers the new mod and (for the API)
   installs its schema.
4. Sign in as an Administrator, open the Mods page, and enable "Feature
   Board". It can be disabled again the same way at any time, with no restart
   required either way. While disabled, its pages, stylesheet and nav link
   all disappear (within about 10 seconds); its data is kept.

## Upgrading to 1.0.1

1.0.1 is a bug-fix release. Replace both DLLs and restart both processes.

- In core's dark scheme, the board's columns stayed light grey, so their
  headings were hard to read and the cards didn't match the rest of the
  site. The columns and cards now use core's dark colours.
- MvcBB.API re-runs the mod's install step because the version changed. It
  makes no schema changes in this release.

## Upgrading to 1.0.2

Replace both DLLs and restart both processes.

- Administrators get a new request alert, like core's open-reports alert: a
  kanban icon in the navbar with a badge counting the requests waiting in
  Requested, and a toast when a new one arrives while they're on a page.
  See "On core pages (view slots)" above. It needs MvcBB 1.3.1 or later
  (the `layout.navbar` and `layout.toasts` slots). On an older core the
  alert doesn't appear and the rest of the mod works as before.
- Every signed-in member gets a "Feature Board" card on the home page's right
  column with the 5 newest requests (Discarded ones left out). See "On core pages (view slots)" above.
  It uses a new API endpoint, `GET api/mods/featureboard/requests/recent`, so
  replace the API DLL as well as the App's: until the API is upgraded the
  card simply doesn't appear.
- MvcBB.API re-runs the mod's install step because the version changed. It
  makes no schema changes in this release.
