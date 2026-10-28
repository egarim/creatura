# Design: sharing files between cells over the mesh

Status: proposed, 2026-10-28. Nothing here is built. It follows [design-cells.md](design-cells.md), slices 1 to 4.

## The problem, in the owner's words

> The mesh should be used for the agents to share files, binary and text data.

Today the cells share one thing: the work ledger, small signed facts about who is working on what. An agent on the
Surface that produces a report, a trace, a screenshot or a build cannot hand it to an agent on the owner's laptop. People copy
files by hand, or with `scp`, and nothing records who shared what, for which piece of work, or whether it arrived
intact.

## Three principles

1. **The ledger carries the record, never the bytes.** Every cell keeps the whole ledger (decision 2 of the cells
   note), so a file in it would be copied to every cell forever. The ledger gets a small signed entry that names the
   file by its hash. The bytes travel separately, only to the cells that ask.
2. **The hub moves files but cannot read them.** The hub is meant to move to a VPN server the owner does
   not want trusted with content. Files are encrypted before they leave the cell that shares them, and only their
   recipients can open them.
3. **Nothing new to trust.** A file is authentic because a cell an owner endorsed signed its entry, and intact
   because its hash matches. The hub answers only signed requests from endorsed cells, as it does for the ledger,
   and revocation covers files too.

## The parts

### An encryption key for each cell

A cell's key today signs; it does not encrypt. Each cell gains a second key pair, ECDH on P-256, generated on its
vessel and kept owner-only beside the signing key. Like the signing key, it never leaves the vessel. Its public half goes into the cell's
hello. The hello is signed by the cell, and the cell is endorsed by the owner, so the encryption key is the owner's
cell's without a new endorsement. A cell whose hello has no encryption key cannot receive files until it says a new
hello, which every command does on its next run.

### Sharing a file

`creatura cell share <file> [--claim <id>] [--to <cell,...>]`:
1. reads the file, hashes it (SHA-256 of the plain bytes), and picks a random 256-bit file key;
2. encrypts with AES-256-GCM a small envelope: the file's name, its content type, and the bytes;
3. wraps the file key for each recipient: an ECDH agreement between a fresh ephemeral key and the recipient's
   encryption key, through HKDF, then AES-GCM over the file key;
4. uploads the ciphertext to the hub, which stores it under the SHA-256 of the ciphertext;
5. writes a signed **Artifact** entry to the ledger: the ciphertext hash and size, the plain hash, the claim it
   belongs to, and the wrapped keys by recipient cell. It holds no name and no content type: those are inside the
   encrypted envelope, so the hub learns neither.

Everything here comes with .NET (`AesGcm`, `ECDiffieHellman`, `HKDF`), so `Creatura.Cells` stays package-free.

### Fetching a file

`creatura cell fetch <artifact id> --out <file>`:
1. finds the Artifact entry in its own ledger (it arrived with sync) and its own wrapped key in it;
2. downloads the ciphertext from the hub by its hash, with a signed request;
3. checks the ciphertext hash, unwraps the file key, decrypts, and checks the plain hash;
4. writes the file. A cell keeps the ciphertext it fetched under `<home>/files/<hash>`, so a second fetch needs no
   network.

`creatura cell files [--claim <id>]` lists the artifacts in the ledger that this cell can open, with name and size.

### At the hub

`PUT /files/<hash>` and `GET /files/<hash>`, both behind the signed-request check. The hub stores the bytes only if
they hash to the name given, so an upload cannot be filed under another file's name. It keeps files up to a size
limit and for a retention period, decision D3 and D4. When a file expires, its Artifact entry stays in the ledger:
fetch then says the file expired, not that it is corrupt.

### For agents, not only the command

The same steps are a small API in `Creatura.Cells` (share, list, fetch), so a creatura's loop or a host tool can
share what it produces: a run's trace, a report, a patch. The Claude Code hooks do not share files on their own;
that would need its own decision about what leaves a session.

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| D1 | encryption | (a) a fresh key per file, wrapped for each recipient; (b) one fleet key every cell holds | **(a)**: a revoked cell is simply not a recipient of new files, and a lost key exposes only what it was given. With (b), every revocation means rotating the fleet key on every cell |
| D2 | who receives a file by default | (a) every endorsed, unrevoked cell in the sharer's ledger; (b) only cells named with `--to` | **(a)**, with `--to` to narrow it. A cell that joins later cannot open older files unless someone shares them again |
| D3 | size limit per file | 16 MB, 64 MB, 256 MB; larger files need chunked upload, which is later work | **64 MB** to start |
| D4 | retention at the hub | until deleted; a fixed period; a storage quota with the oldest removed first | **90 days** from the upload, then deleted. The Artifact entry stays, and cells keep the copies they fetched |
| D5 | who files can be shared with | the owner's own fleet only; also other people and their agents | **the owner's fleet only**. Other people need a trust design of their own, a later note |
| D6 | what the hub may see | (a) hashes, sizes and recipients only, name and type encrypted; (b) names in the clear, so the hub can list them | **(a)** |

One thing the hub sees regardless: the ledger itself, which it stores unencrypted, so it knows which cell shared an
artifact for which claim and when. Encrypting the ledger at the hub is a separate question, not part of this note.

## Pre-registered tests, written before the code

| id | claim | falsifier |
|---|---|---|
| F1 | a file shared by one cell is fetched byte-identical by another through the hub, text and binary alike | any difference in bytes, name or type |
| F2 | the hub's database and files hold neither the content nor the name of a shared file | a run of the plain bytes, or the name, found in anything the hub stores |
| F3 | a file changed by one byte at the hub is refused at fetch, and nothing is written | a changed file written out |
| F4 | a cell that is not a recipient, a revoked cell, and another owner's cell cannot fetch or open a file | any of them ends with the plain bytes |
| F5 | an upload whose bytes do not hash to its name is refused by the hub | the bytes stored under that name |
| F6 | a file over the size limit is refused before upload, and the hub refuses an upload over it | a file over the limit stored |
| F7 | after retention the hub has deleted the file, the Artifact entry remains, and fetch says expired | the file still served, the entry gone, or a corrupt-file error |
| F8 | a fetched file is opened again without the network | a request to the hub on the second fetch |

And one live run, not a test: the second machine shares a file, and the owner's laptop fetches and opens it; then the owner's laptop shares
one, and the second machine does the same.

## What this note does not cover

- Sharing with people or agents outside the owner's fleet (D5).
- Chunked or resumable transfer for files over the size limit.
- Files moving between cells directly, without the hub.
- Encrypting the ledger itself at the hub.
- Hooks that share files from a Claude Code session on their own.
