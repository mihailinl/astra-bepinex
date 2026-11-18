# The Astra game bridge protocol

> This page is synced from the engine's BRIDGE.md on 2026-11-18. The engine that implements the
> protocol (astra-avatar-engine) keeps the source of truth; `tools/check-protocol-sync.sh` compares
> this copy with it whenever the engine is checked out beside this repository.

The engine can run with **no window** and draw Astra into a game instead: a game mod sends the
engine the game's camera and where she stands, and the engine publishes her picture (colour plus
per-pixel depth) into shared memory for the mod to composite into the game with a depth test, so
the game's walls hide her and she hides the game. The game owns the WORLD (collision, navigation,
where she is). The engine owns HER (pose, animation, face, voice). This page is the contract a mod
is written against.

In this repository, `src/Astra.Bridge.Core` is the game side of it for .NET (no Unity in it: any .NET
mod can use it), and the Unity foundation is built on that. The foundation sends `hello` (with
`caps: ["warn","capture"]`, `params` when an integration is registered, and `bones` when it asks
for her bones — `UseBones`, read from each frame's flag-256 section, which `HoldItem` follows), `cam`, `avatar`, `param`, `play` /
`trigger` / `resume` and `light`, reads the frame ring, and prints the engine's `warn` messages. From
0.8.0 it also captures: while the engine names an `inbox` (the hello reply's, then — from an engine
whose reply lists `capture-live` — each `{"t":"capture","inbox":…}`; the foundation's hello lists
`capture` in its `caps`), an integration's `CaptureWhile` writes `.vrma` takes there and sends
`clip_ready` (`src/Astra.Bridge.Core/Capture`).

**What a released engine has (2026-10-14).** BRIDGE.md describes the engine as it is built today,
ahead of its newest release, v0.2.0 (2026-10-07). v0.2.0 has the `caps` words `views`, `caster`,
`light`, `light.fog`, `params`, `cues`, `game-level` and `lenient`. Everything else below comes with
the engine release after v0.2.0: the `caps` words `crop`, `hello.params`, `warn` and `capture`; the
hello's `crop`, `caps` and `params`; the `warn` message; `clip_ready` and the reply's `inbox`; and a
parameter her set does not declare being REMEMBERED (v0.2.0 ignores it). So the example reply's
`"ver":"0.2.0"` beside those words shows a newer engine: decide by the reply's `caps`, never by
`ver`. (This block is this repository's own: update it at each engine release.)

<!-- Everything below is generated from the engine's BRIDGE.md by tools/check-protocol-sync.sh --write: edit BRIDGE.md, not this. -->

## Running it

**Nothing to run, usually:** the desktop pet listens on the bridge's port, and a game that connects
there and says `hello` holds her: the reply is the ordinary `hello` and the connection stays open.
**She leaves the desktop at your FIRST `cam`**, within a frame or two: her window is closed, so
nothing is left drawing over your game, and the same process draws her into it. That first `cam`
itself always lands before the switch and is NOT drawn (about one frame), nor is anything else you
send until the switch is done — the next frame's `cam` is. Until your first `cam` your connection
holds the slot (no other game can take her) and she stays on the desktop, and nothing you send
acts on her there: your `param`s, `light`, `text`s, `activity`s, `avatar` and your hello's game
packs are held — the latest of each — and applied at that first `cam`, before it; a `play`,
`trigger` or `resume` is dropped (`warn` `not_in_game`); your hello's `params` declarations and a
`clip_ready` go through at once. So a foundation may connect in its main menu or a loading screen,
keep the link alive with `param`s, and send a `cam` once a player exists. When your game closes, crashes or goes quiet
and does not come back within 8 s, she returns to the desktop, with the Studio reopened if
it was open. `WGPU_GAME_SWITCH=off` keeps her on the desktop and opens no port. **Astra's owner
can take her out of a game at any moment** (toggling the companion off, logging out, or the daemon
restarting it) — your connection is then dropped exactly as a crash or a quiet socket would be.

**The owner can turn games off** — the engine's own setting `bridge.games` in `settings.json`
(default on), the ⚙ panel's "games" row, or Astra's "Let games bring Astra in" switch (the daemon's
`gameSwitch`). Off, every `hello` is refused with `Astra's owner has turned games off`, and a game
that holds her is let go at once (she goes home without the 8 s wait); the port stays bound, so
your retries stay quiet. Retry every couple of seconds and dedupe the refusal in your log. Two more
refusals a mod may meet at the door: `Astra is starting` (for a second or so at launch — retry), and
the crash pause — if the engine died while she was inside a game, the next run refuses every game
for its whole life (so a crash inside a game cannot loop). That pause lasts exactly ONE run,
however it itself ends: the run after it is back to normal whether it exits cleanly, is closed by
the owner, or even dies again. To run game mode directly (and stay in it):

```bash
WGPU_PROTO_WINDOW=bridge astra-avatar-engine
```

| var | default | meaning |
|---|---|---|
| `WGPU_PROTO_WINDOW=bridge` | — | **The switch.** No window; serve a game instead. Works on Linux, Windows and macOS. |
| `WGPU_BRIDGE_PORT` | `25600` | WebSocket port, on `127.0.0.1` only. |
| `WGPU_BRIDGE_SHM` | `astra-frame` | Name of the frame ring: `/dev/shm/<name>` on Linux (Wine/Proton sees `Z:\dev\shm\<name>`), `Local\<name>` on Windows, `$TMPDIR/<name>` on macOS. `[A-Za-z0-9._-]`, never a path. On Linux/macOS a file at that name owned by ANOTHER user is stepped around: the ring is made at `<name>-<uid>` instead — open the ring the `hello` reply's `shm` names, never a name of your own. |
| `WGPU_BRIDGE_MAX` | — | The developer's override of the ring's size. Unset, the ring is made at the size your `hello.want` names (at most `3840x2160`), or `1920x1080` for a game that names none. Set (an even `WxH`, 16–8192), it is the size for a game that names none AND the most any game gets. A camera bigger than the ring is scaled down, aspect kept. |
| `WGPU_BRIDGE_TOKEN` | — | Optional shared secret: when set, nothing but `hello` is honoured until a `hello` carrying `"token"` arrives. `ASTRA_BRIDGE_TOKEN` is the same secret under the name Astra's daemon passes at spawn; when both are set it wins. **Both stop counting once Astra sends its per-profile tokens** (§Security): from then on only a token Astra registered for your game's profile lets a `hello` in. |
| `ASTRA_COMPANION_MODE=games` | — | Astra's **games-only** spawn mode: no desktop pet, only games (like `WGPU_PROTO_WINDOW=bridge`), and the process exits by itself 8 s after the game holding her lets go — or after 10 min if no game ever came. `desktop` (or unset) is the ordinary pet. |

A default (daemon) build is auth-gated by Astra like the desktop pet; a `--no-default-features`
build runs standalone.

**The Companion Studio beside the game:** a build with the `debug-ui` feature (`cargo build
--release --no-default-features --features debug-ui` for a standalone one) opens the Studio in its
own window when started with `WGPU_PANEL=on`. It edits the Astra in the game live — states, clips,
parameters, lighting — and never appears in the game's picture. There is no Studio key inside a game:
closing the window closes it for the run. While bridged the engine **never writes `settings.json`** (it is the desktop
pet's) and never captures the desktop for her lighting.

## Coordinates

**glTF: right-handed, +Y up, metres.** Convert your game's world into this (Death Stranding:
`(x, z, −y)`). Use a floating origin near the player when the world is large — positions are `f64`
on the wire and the engine works relative to her feet, so precision near her never suffers, and a
rebase is just a jump.

## The WebSocket (`ws://127.0.0.1:25600/`)

Text frames of compact JSON, `"t"` first. The engine is the server; **one** game controls her at a
time (a second connection gets an `error` and is closed). A handshake carrying an `Origin` header
is refused (browsers are not allowed in); native clients do not send one.

### Game → engine

```json
{"t":"hello","v":1,"client":"astra-unity/PEAK","game":"PEAK","foundation":"0.5.0","integration":"astra.peak","shadow":"caster",
 "want":[2560,1440],"caps":["warn"],"params":[{"name":"climbing","type":"bool"},{"name":"speed","type":"float"}],"bones":["rightHand"]}
{"t":"cam","id":1234,"pos":[x,y,z],"fwd":[x,y,z],"up":[x,y,z],"fovY":45.75,"w":1920,"h":1080,"echo":[a,b,c],"view":0}
{"t":"avatar","id":0,"pos":[x,y,z],"fwd":[x,y,z],"up":[x,y,z],"vel":[x,y,z],"anchor":"hips","scale":1.2,"params":{"speed":1.4,"airborne":false}}
{"t":"param","set":{"speed":0.0}}          // parameters on their own (null = back to the default)
{"t":"play","state":"seat"}               // enter a state of her animation set by name
{"t":"trigger","name":"music_mid"}         // raise a token into her graph
{"t":"resume"}                             // hand her back to the graph's default state
{"t":"light","sun":{"dir":[x,y,z],"color":[r,g,b],"intensity":1.0,"visible":1.0},"ambient":[r,g,b],
 "ambientCube":[r,g,b, r,g,b, r,g,b, r,g,b, r,g,b, r,g,b],
 "lamps":[{"pos":[x,y,z],"range":8,"color":[r,g,b],"intensity":12.5,"spot":{"dir":[x,y,z],"cos":[0.9,0.97]},"held":false}],
 "ambientSrc":"global","open":0.4,"suns":[{"dir":[x,y,z],"color":[r,g,b],"intensity":0.3,"visible":1.0}],
 "look":{"floor":0.2,"ceiling":1.4},"fog":{"color":[r,g,b],"mode":"exp","density":0.1}}
```

- **`hello`'s `client`** — a stable id for your mod (`<foundation>/<game>`, e.g. `astra-unity/PEAK`):
  Astra keys the owner's consent on it. **`game`** (optional) — the game's display name, shown to
  the owner ("Astra is in PEAK"); absent, it is the part of `client` after its last `/`.
  **`foundation`** / **`integration`** (optional) — your version and your per-game integration id.
  **Once Astra manages per-profile tokens (§Security) your `integration` is the one ASTRA registered
  for your token, whatever the hello says** — a different one is logged and ignored.
  All four are shown and logged, so the engine drops control and bidi characters and keeps at most
  64 characters of each; nothing is refused for them.
- **`hello`'s `shadow`** (optional) — which of her two shadows you read, because each costs the engine
  every frame: `"sun"` (her view from the sun in plane 3, flag 32 — the DS add-on's), `"caster"`
  (her shadow caster, flag 64 — the Unity foundation's), `"both"` or `"none"`. Absent = `"sun"`, what
  every client got before the caster existed. Any other word falls back to `"sun"` with one note in
  the engine's log (an engine from before `caps` refused the hello instead).
- **`hello`'s `crop`** (optional, `true`) — send only her RECTANGLE of each picture (flag 128, the
  slot's rectangle at +116): she covers a few percent of your frame, so this is a few hundred KB a
  frame to copy instead of ~16 MB at 1080p. Safe to send to any engine — one that does not crop
  ignores it (its `caps` has no `crop`) — so read flag 128 on every frame either way. The rectangle's
  size moves in 64-pixel steps and seldom shrinks, so textures you upload it into are rarely
  re-made; its position follows her every frame.
- **`hello`'s `want`** (optional, `[width, height]` in pixels) — the LARGEST picture you will ask
  for: your back buffer's (or screen's) size, the `w`/`h` your biggest `cam` will carry. The frame
  ring is made for it, so a 2560x1440 game gets a 2560x1440 picture instead of a 1920x1080 one
  scaled up (blurry, with fringe pixels where her outline and her depth stop matching your
  scene's). Rounded up to even, at least 16, at most **3840x2160** (or the `WGPU_BRIDGE_MAX` pin,
  which always wins). Without it you get 1920x1080, as before. The reply's `maxW`/`maxH` are what
  you GOT — always the ring's real size; it can be larger than you asked (the ring never shrinks
  while the engine runs: a ring an earlier game made bigger stays so) or, under a pin, smaller.
  **Your window grew?** Send a new `hello` with the new `want` on the same connection: a larger
  one re-makes the ring before the reply, which names it again — each growing side by at least a
  quarter, so a window dragged larger costs a few re-makes, not one per frame (her picture still
  follows your `cam` size, never the ring's) — stop reading your mapping when
  you send that hello and map the reply's `shm` afresh (the layout changed). On Windows close your
  mapping BEFORE that hello: a ring a reader still holds cannot grow there (the engine then keeps
  the old one, header untouched, and the reply's `maxW`/`maxH` say so). A smaller `want` changes nothing. Send it
  when the reply's `caps` lists `want` (an older engine ignores it anyway); an invalid one (not two
  whole numbers in 1..8192) is set aside with one note in the engine's log, as if absent. What
  each size costs is in *The frame ring* below.
- **`hello`'s `bones`** (optional, a list of at most **8** VRM humanoid bone names — `"rightHand"`,
  `"leftHand"`, `"head"`, `"hips"`…: VRM 1.0 humanoid bone names, camelCase and case-sensitive —
  `rightHand` is a name, `RightHand` and `Hips` are unknown) — bones whose POSE you are
  given with every picture of her, in the same slot (flag 256, *The frame ring* below): to draw your
  own item in her hand, in lockstep with the picture, never a frame behind it. More than 8, or
  anything but a list of strings, refuses the hello with an `error` naming `bones` (an accepted
  earlier hello on the connection stays in force). A name that is no humanoid bone does NOT refuse
  it: it is left out of the stream, the engine notes it once in its log and, if your hello's `caps`
  lists `warn`, tells you (`bone_unknown`, one per name, at most 8 for the connection). The reply's
  `bones` is the list streamed, in order, and `bones_rev` is its REVISION, a number from 1 to 8191
  (then 1 again) that is unique across connections within that wrap — a reconnect never gets the
  number an earlier session's frames still carry in the ring — and changes only when the list does:
  a `hello` saying the same list again (a game may hello every frame) is answered with the same
  `bones_rev`. A later `hello` on the connection replaces the list (one without `bones` streams
  none). Frames captured before the engine applied a new list still carry the old one: **use a
  section only when its revision equals the last reply's `bones_rev`** and skip the item for the
  frames in between.
  Send it when the reply's `caps` lists `bones` (an older engine ignores it anyway).
- **`hello`'s `caps`** (optional, a list of words) — what YOUR side understands beyond the first
  contract, so the engine sends an optional message only to a game that reads it. Today two words:
  **`warn`** — send it to be told what went wrong with what you sent (the `warn` message below);
  **`capture`** — send it to hear the owner's capture switch WHILE you play (the `capture` message
  below, from an engine whose reply's `caps` lists `capture-live`); without it you learn the switch
  only from a hello reply's `inbox`.
- **`hello`'s `params`** (optional) — the animator parameters you SEND, each
  `{"name": …, "type": "float"|"bool"}`, names spelled as a set's `[parameters]` keys (an identifier
  of at most 32 characters), at most 64. The engine keeps the list, per `integration` (none, nothing
  kept), in the owner's user folder as `games/<integration>.json`, so Astra's Studio can show what
  your game sends before the owner has ever started it — and offer to make her react to it. A bad
  entry is set aside with one note in the engine's log; the rest are kept. A `params` that is not a
  list keeps nothing; an empty list says you send none. Send it only when the reply's `caps` lists
  `hello.params` (an older engine ignores it anyway).
- **A message type the engine does not know** is set aside with one note in its log per connection
  — never answered with an `error` (an engine from before `caps` sent one per message: send an
  optional message only when the hello reply's `caps` lists it).

- **`cam`** — once per game frame. `pos` is the eye; it looks along `fwd`; `up` fixes the roll.
  `fovY` is the VERTICAL field of view in degrees (1–179); the projection is a plain symmetric
  perspective at `w/h` aspect, no jitter. `w`/`h` is the picture size you want (keep your aspect;
  ~1080p is plenty). `id` is your frame id and comes back in the frame. `echo` (≤ 8 numbers, the
  first 3 kept) is copied into the frame verbatim and never interpreted — put whatever your
  reprojection needs there. **A frame is drawn for each `cam`**; several since the last draw
  collapse into the newest. **She never draws ahead of the GPU**: no picture is started while the
  GPU is still drawing the last one, so when your game keeps the GPU busy she draws FEWER pictures
  (each from the newest `cam` of its view), never a growing queue of old ones — reproject the
  picture you have, as for any frame that arrives late.
- **`view`** (optional, a whole number 0–7, default 0) — WHICH of your cameras this is, when you
  film her through more than one (your main camera is view 0; a mirror, a handheld camera, a
  security monitor are others). Each view keeps its own newest `cam`, and one never supersedes
  another. After a `cam` the engine draws EVERY view whose newest `cam` it has not drawn yet — all
  of them with the SAME pose of her (her animation advances once, then each further view is drawn
  at that same instant), view 0 first and published before the next is drawn. A `view` out of
  range is refused with an `error`, never treated as 0. What changes per view:
  - **The picture size is view 0's** (until a view 0 arrives, the first view's): every view is
    drawn into that one size — a view's own `w`/`h` only give its projection its ASPECT (`w/h`),
    so its pixels are not square when its aspect differs. Map its picture back with ITS aspect:
    uv ↔ NDC is linear on both axes, the projection is the plain symmetric perspective at its own
    `fovY` and `w/h`. Want a sharper mirror? Send view 0 a larger size, not the mirror.
  - **A new size of view 0 must HOLD for 150 ms** before the picture takes it (a window being
    dragged asks for a new size every frame, and the engine will not rebuild its targets every
    frame). Meanwhile view 0 is drawn like any other view: into the previous picture size, its
    projection at its NEW aspect — so map it back with its own `w/h`, as above. The very first
    `cam` of a session is taken at once.
  - **Her gaze follows view 0** (the player): she does not look into a mirror's lens.
  - Each view's frame is tagged with its view in the ring (below); send the `avatar` once per game
    frame, before the first `cam` — it is shared by every view.
  - **Send a frame's `cam`s together** — back to back, ideally in one write. The engine draws
    what has arrived when it wakes; a `cam` that lands after it began drawing gets a wake of its
    own, and her pose there is a few milliseconds later than in the views drawn before it.
- **`avatar`** — where she stands: `pos` is her FEET, `fwd` her facing, `vel` m/s (optional; her
  hair and clothes react to her measured motion either way). **`up`** (optional) completes the
  basis: with it she takes the full orientation `fwd`/`up` describe — a vehicle's pitch and roll
  tilt her with the seat — and the rotation is about the anchored point, so a pinned pelvis stays on
  the seat point; without it she stays upright, turned by `fwd`'s horizontal part. `up` need not be
  exactly perpendicular to `fwd` (it is re-orthogonalised), but must not be parallel to it.
  **`scale`** (optional, 0.05–20, default 1) is her SIZE in your world, uniform, applied about the
  anchor (a pinned pelvis stays on its seat). Her published depth stays in your metres. Her
  animation set reads her speed at her size — `speed` is declared `body_relative`, so a 1.5× Astra
  at 1.05 m/s walks exactly like a 1× one at 0.7, legs paced to her longer stride, and starts to run
  later. Keep sending her real world speed; do not divide it yourself. **`anchor`** (optional)
  says which point of her `pos` names: a VRM humanoid bone (`"hips"`, `"head"`, `"leftHand"`…)
  pinned to `pos` in her current pose, or `"feet"` (the default). In a vehicle seat send the seat
  point with `"anchor":"hips"` and play `seat`: the clip only sits, the anchor puts her pelvis on the
  seat. A change of anchor is eased over a few frames, never a jump.
- **`play` / `trigger` / `resume`** — the SAME animation cues Astra's daemon sends. `play` enters a
  state by name and HOLDS it until the graph leaves it (a played state is hers; nothing the engine
  decides takes it back); `trigger` raises a token and takes whatever edge the current state
  authors for it (or nothing); `resume` enters the graph's default state. An optional `"set"` names
  the animation set you expect her to wear — a cue for another set is declined; it must be printable
  text as it is (no control or invisible characters, no leading or trailing space) or the message is
  refused. The built-in set's
  states: `idle`, `sit` (a window-edge perch — not for games), `drag`, **`seat`** (a chair: sits
  down, sits, stands up; `trigger unseat` stands her up through the outro, `resume` at once),
  `dance_slow` / `dance_mid` / `dance_fast`, and **`move`** / **`air`** (below) — which you never
  `play`: her parameters take her there. **Work and items** — PLANNED: these states and the parameter `carrying` are not yet in the
  built-in set (until they ship, a `play` of one is answered `state_unknown`), and when they do they
  are data, provided by the built-in set: `strike` (a
  weapon swing), `chop` (an axe at a tree), `mine` (a pick at rock), `pick_up` (from the ground),
  `give` (hands something over) and `guard` (a shield or a block), each a one-shot you `play`; and
  the parameter **`carrying`** (bool: something is in her hand — her arm holds it; draw the item
  yourself at her `rightHand`, from `hello.bones`).
- **`params`** (on `avatar`, or alone as `param`) are her animation set's ANIMATOR PARAMETERS —
  **raw facts, never decisions**: send how fast she moves, not `gait: "run"`. What a fact means —
  which state, which clips, how fast her legs go — is decided by her animation set, which a user can
  edit or replace (a set could declare `altitude` and author flying). A number, a bool (1/0), or
  `null` to hand it back to the set's default. A name the set does not declare is REMEMBERED, not
  ignored: when a set (or, later, a pack for your game) that declares it becomes active, it starts
  from your last value — so the order of your hello, your first `param` and that set's arrival does
  not matter. The engine says so once in its log and, if your hello's `caps` lists `warn`, tells you
  (`param_undeclared`). When your game lets go of her (a close, a crash, going quiet) every
  parameter you set is forgotten and returns to the set's default. The built-in set declares two:

  | parameter | type | what to send |
  |---|---|---|
  | `speed` | float, body-relative | her ground speed in YOUR m/s (the length of `vel` along the ground is right); read at her `scale` |
  | `airborne` | bool | she is off the ground — a jump or a fall. Debounce it: a flag that flickers for a frame on rough ground makes her hop. |

  Above `speed` 0.15 she is in **`move`**: one blend over walk (0.7 m/s) → jog (2.4) → sprint (4.0),
  her legs paced to the speed you send so her feet do not skate (up to 1.5× past the last). Below
  0.08 she stands. `airborne` puts her in **`air`**; landing while still moving goes straight back
  into her stride, landing standing plays the landing. The clips are IN PLACE: you move her (`pos`),
  the clip moves her legs.
- **`text`** — `{"t":"text","slot":"reminder","text":"…"}`: set one TEXT SLOT, the string a pack's
  prop shows on its label (`[effects.X.label]`, `docs/reminder-peek.md`). The daemon's
  `CompanionCommand.text_slot` (24) through the same constructor: a slot name of letters, digits,
  `_`, `-`, `.` (at most 32) or the message is refused; the text is cleaned, never refused (at most
  8 lines, 512 characters). `text` absent or `null` clears the slot. A game holds at most 16 slots
  written at once: a NEW slot past that is refused (`warn` `text_slots_full`; clear one to make
  room), and every slot your game still holds is cleared when it lets go of her.
- **`activity`** — `{"t":"activity","id":"g/1","phase":"start","source":"g","label":"…","category":"shell","tool":"…","fields":{"cmd":"…"},"event":"","ok":true}`:
  one ACTIVITY's news (`docs/work-windows.md`) — something being done, shown as a window above her
  keyboard when the worn pack maps its `category` (`[activities]`). The daemon's
  `CompanionCommand.activity` (26) through the same constructor: `id` and `phase`
  (`start`/`update`/`event`/`end`) required, an `event` needs its token, `fields` an object of
  strings; values are cleaned, never refused (indentation kept, blank lines dropped, the last whole
  lines within 32 lines and 1024 characters). Your `id` is yours alone: the engine files it under
  `game/` (so `id` + 5 must stay within 96), and a `category` that is one of Astra's ANSWER cards
  (`confirm` — an AI action's yes) is refused. What you leave running when you disconnect is ended
  for you (failed); after Astra's own link blips, re-send `start` for what still runs.
- **`light`** — the light of your world around her. Send what your engine KNOWS, raw; the engine
  makes it into ONE light for her whole body — a level held inside a band, a lit and a shade
  colour and a direction — measured at her chest, eased the way an eye adapts (brighter in about a
  third of a second, darker in about one), and never dependent on your camera: turning it never
  changes her. Why one light and not each lamp per pixel: your light data is taken before your
  exposure, fog and grading, often partial, and her picture lands after all three — a bounded
  per-object light (what VRChat's avatar shaders do) reads right in every game, including the ones
  whose data is wrong. She is never black (the band's floor, a dim figure where your data says
  darkness) and never blown out (its soft ceiling, and a roll-off of her highlights); her ink line
  and a flat rim take her light's LEVEL, never its colour (a material authored to follow the light
  follows its bounded, partly greyed colour). A sun above her horizon is her key exactly, so her
  self-shadow and her sun view (below) are cast along it. Desktop lighting is untouched. It holds
  until the next `light` — send it on change or at ~10 Hz; `{"t":"light"}` with nothing in it
  hands her back her own light.
  - **`sun`** — your main directional light. `dir` is the unit direction TOWARD it (glTF world);
    `color` linear RGB, normalised; `intensity` 0..16 (1 ≈ a daytime sun against her own key);
    `visible` 0..1 how much of her it reaches — YOUR occlusion (a rock, a roof), which her own
    self-shadow cannot see: send it raw, the engine eases it.
  - **`suns`** (optional, at most 3) — your OTHER directional lights (a fill light), each with its
    own `visible`.
  - **`ambient`** — the linear sky/environment light around her, one colour. **`ambientCube`**
    (optional) — the same BY DIRECTION, what a surface facing world +X, −X, +Y, −Y, +Z, −Z receives,
    18 numbers on the same scale; read it off your light probes at her chest (Unity:
    `LightProbes.GetInterpolatedProbe` evaluated in the six directions). It wins over `ambient` for
    her level; its SHAPE moves her surfaces at most ±15% in luminance, never her colour.
  - **`ambientSrc`** — where your ambient came from: `"local"` (a probe at her — it already knows the
    roof over her), `"global"` (one value for the whole scene, the sky's: the same in a house as
    outdoors) or `"none"` (you could read nothing; with a sun the engine estimates the sky from it).
    A message that carries `ambientSrc`, `ambientCube`, `lamps` or `suns` describes your WHOLE
    light: a part left out is absent — no `sun` is no sun (a cave), and
    `{"t":"light","ambientSrc":"none"}` alone is a world with no light at all (the band's floor,
    not her own light back). Only a message of `sun` and `ambient` alone (an older client's) keeps
    her own key or her own fill colour where it leaves one out. Absent `ambientSrc` = `local`.
  - **`open`** (optional, 0..1) — the share of the sky open above her (rays up that meet nothing
    casting a shadow). It closes a GLOBAL sky under a roof (down to 30% of it) — only where a sun
    says there is a sky, now or earlier in the session. Absent = open.
  - **`lamps`** (optional, at most 8, strongest first) — the point and spot lights that reach her,
    none behind a wall (test that yourself). Light at a distance `d` is
    `color · intensity / d² · clamp(1 − (d/range)⁴, 0, 1)²` — the inverse square that reaches zero
    at `range`, URP's and HDRP's own law — times a spot's cone: full inside `cos[1]` (the inner
    half-angle's cosine), none outside `cos[0]` (the outer's), smooth between; `spot.dir` is where it
    points. `intensity` is on the sun's scale (a lamp whose `intensity/d²` is 1 at her lights her
    like a sun of 1), 0..10000; `range` metres, > 0. A pipeline with another falloff (Unity's
    Built-in) sends the intensity that makes this law give, at her chest, what its own gives there.
    Each is measured ONCE at her chest. **`held`** — the player holds it (a flashlight on the
    camera): its light follows where the player looks, so it does not light her (the engine's
    `WGPU_WORLD_HELD` weighs it back in for an A/B). Do not send a light your probes already carry
    (Unity: a Baked one) — it would light her twice.
  - **`look`** (optional) — your game's taste for her band: `floor` 0.05..0.40 (the dimmest she gets,
    default 0.20) and `ceiling` 0.8..2.0 (the brightest she approaches, default 1.40), as multiples
    of her albedo. Absent = the engine's. Set it only where a live look at your game asks for it.
  - **`fog`** (optional) — the haze YOUR picture composites AFTER hers, which hers never sees
    otherwise (a crisp cut-out in grey haze): `{"color":[r,g,b], "mode":"exp"|"exp2"|"linear",
    "density", "start", "end"}`. `color` is LINEAR RGB, 0..16 (what the fog itself looks like — put
    on her scale the same way her light is, so a dark fog does not make her a black hole and a
    bright one does not blow her out). `mode` picks which of `density` (`"exp"`/`"exp2"`, per metre)
    or `start`/`end` (`"linear"`, metres, `end` > `start` ≥ 0) it reads; an unknown mode is refused.
    Absent = no fog. A `fog` alone is still a `light` message (it lights her with nothing new, hazes
    her with something), never a message that is `is_whole`'s kind of whole. `WGPU_WORLD_FOG=off`
    turns it off for a live A/B. Send it only when the hello reply's `caps` lists `light.fog` — an
    engine from before that word drops it silently.
- **`clip_ready`** — a move your runner CAPTURED from the game (its own animator sampled into a
  VRMA: each bone measured against the rig's BIND POSE as its rest — the skinned mesh's bind pose
  under the Animator, straightened to a T-pose — kept in place: the take carries no root motion), offered to the
  owner:
  `{"t":"clip_ready","file":"climb-1760000000.vrma","name":"climb","for":"climbing"}`. Only when
  the hello reply carries **`inbox`** — the owner has switched capture on for YOUR integration (off
  by default) — and only after a hello that named your `integration`. Write the `.vrma` into that
  directory first (as `<name>.part`, renamed to its final name when complete), then send this.
  `file` is the bare file name in `inbox` (`[A-Za-z0-9._-]`, ending `.vrma`, at most 128
  characters — no path); `name` is the clip's name, 1–48 of `[A-Za-z0-9_.-]` (so the file named
  after it is always echoed whole in a refusal), and `for` the parameter it is a move for, 1–64 of
  the same. The engine copies the file (it never plays it from `inbox`) into the
  owner's own pack for your game ("My PEAK moves", created if missing) as the clip
  `captured-<name>` — so it replaces nothing she plays — and asks the owner whether to use it for
  `for`. Refused, it tells you a `clip_refused` warning (below) with `why`: capture off, a file that
  is not a real file directly in your `inbox`, over 32 MiB, not a VRMA animation, or 32 of your
  captures already waiting for the owner's answer. At most 64 per connection, 500 ms apart: one
  over that budget, and one that names its `file` but has a bad `name` or `for`, is a
  `clip_refused` too, never an `error` — only a `clip_ready` whose `file` itself is missing or
  unreadable is answered with an `error` (there is nothing to name). The refusal's `name` is your
  `file`, WHOLE (byte for byte while your file keeps to `[A-Za-z0-9._-]`): match it to the file you
  offered and remove that file. The engine removes your
  file from `inbox` once its copy has landed; a refused file is left for you. A captured clip
  never leaves the owner's machine: Astra will not export a pack holding one. Send it only when the
  hello reply's `caps` lists `capture`.
- **When your game lets go of her** — it closes, crashes, or sends nothing for 10 s — every
  parameter it set returns to its default, if it cued her graph she returns to its default state,
  and if it lit her she gets her own light back. The next game finds her standing.
- A message that fails validation (a non-finite number, a zero `fwd`, a fov out of range, a size
  over 8192…) is refused with an `error` naming the field; the previous camera stays in force.

### Engine → game

```json
{"t":"hello","v":1,"engine":"astra-avatar-engine","ver":"0.2.0","shm":"/dev/shm/astra-frame","maxW":2560,"maxH":1440,"views":8,"caps":["views","caster","crop","light","light.fog","params","cues","game-level","hello.params","warn","lenient","capture","capture-live","want","bones"],"inbox":"/home/u/.config/astra/companion/pack/inbox/capture/astra.peak","bones":["rightHand"],"bones_rev":1}
{"t":"error","msg":"\"fovY\" 0.5 is outside 1..=179 degrees"}
{"t":"warn","code":"param_undeclared","name":"climbing"}
{"t":"warn","code":"bone_unknown","name":"sword","why":"not a VRM humanoid bone name (\"rightHand\", \"head\", …): it is not streamed"}
{"t":"warn","code":"clip_refused","name":"climb-1760000000.vrma","why":"capture is off for 'astra.peak' (switch it on in the Studio's Games)"}
{"t":"capture","inbox":"/home/u/.config/astra/companion/pack/inbox/capture/astra.peak"}
{"t":"capture","inbox":null}
```

`inbox` (optional) is your CAPTURE INBOX — the directory a `clip_ready`'s file goes into. It is
there only while the owner has capture switched on for the `integration` your hello named; absent,
do not capture.

**`capture`** — the owner flipped capture for YOUR integration while you play. Sent only when your
hello named an `integration` and its `caps` listed `capture` (the reply's `caps` then lists
`capture-live`), once per real change — a flip of another game's switch, or one taken back before
the engine's next read tick (~50 ms), says nothing. `inbox` is exactly what a hello reply would
name now (created, owner-only): start capturing into it, no reconnect needed. `null` = capture is
OFF: stop sampling NOW, discard any take in progress (and its `.part`), offer nothing more — a
`clip_ready` sent after it is refused. An engine whose reply has no `capture-live` never sends it:
there the switch reaches you at your next hello.

Map the ring named by `shm` in the hello reply. `views` is how many views (`cam.view` 0..views−1)
the engine draws: an engine from before views sends no `views` and takes EVERY `cam` for the main
camera, so send a second view only when the reply has `views` > 1.

**`warn`** — what went wrong with something you sent, told to YOU rather than only to the engine's
log; sent only when your hello's `caps` lists `warn`. `code` is one of `param_undeclared` (a
parameter her set does not declare — remembered, see `params`), `state_unknown` (a `play` naming a
state her set does not have), `cue_wrong_pack` (a cue whose `set` is not the set she wears —
declined), `clip_refused` (a `clip_ready` whose clip was not taken — `why` says why) and
`text_slots_full` (a `text` writing a 17th slot — refused), `not_in_game` (a `play`, `trigger`
or `resume` sent before your first `cam` — dropped, she is still on the desktop) and
`bone_unknown` (a `hello.bones` name that is no humanoid bone — not streamed); `name` is what
you sent (the parameter, the state, the cue's name, the file, the slot, the bone), cut to 64 printable
characters
— a `clip_refused`'s file to 128, so it comes back whole — and `why` (only on a code that needs it)
at most 200. Each distinct `code` + `name` comes once per connection, and at most 16 in all — print
them to your console; nothing needs an answer. `clip_refused` is outside those 16 (a lost one would
leave its file in your inbox for good) and has its own bound: at most 64 per connection, one per
`clip_ready` you may send.

**`caps`** — what this engine accepts beyond the first contract. Send an optional message or field
only when its word is here; an engine from before `caps` (0.1.x) sends no `caps` at all — treat that
as an empty list. The words today:

| word | means the engine accepts |
|---|---|
| `views` | `cam.view` 1.. (several cameras; `views` says how many) |
| `caster` | `hello.shadow` = `"caster"` / `"both"` (her shadow caster, flag 64) |
| `crop` | `hello.crop` = `true` (her rectangle only, flag 128) |
| `light` | the `light` message's fields BEFORE fog: `sun`, `ambient`, `ambientCube`, `ambientSrc`, `open`, `lamps`, `suns`, `look` |
| `light.fog` | `light.fog` — the game's haze on her |
| `params` | `param`, and `avatar.params` |
| `cues` | `play` / `trigger` / `resume` |
| `game-level` | `hello.game` / `foundation` / `integration` — Astra shows the owner which game holds her |
| `hello.params` | `hello.params` — the parameters you send, kept per integration for the owner's Studio |
| `warn` | `hello.caps` = `["warn"]` — the engine then sends you `warn` messages |
| `lenient` | an unknown message type is set aside (noted once), an unknown `shadow` word falls back |
| `capture` | `clip_ready`, into the hello reply's `inbox` (present only while the owner has capture on for your integration) |
| `capture-live` | the engine sends the `capture` message (the owner's switch, while you play) to a hello whose `caps` listed `capture` |
| `want` | `hello.want` — the ring is made (or, at a later hello, grown) for the picture size you name |
| `bones` | `hello.bones` — the pose of up to 8 of her bones in every slot (flag 256); the reply's `bones` lists them |

A new optional message or field gets its own word in the engine release that acts on it — the
game's fog is `light.fog`.

### What Astra is told

Astra's daemon learns, as one small status it shows the owner: whether a game can reach her (and
if not, why: the owner's switch, `WGPU_GAME_SWITCH`, or the crash pause), which game holds her —
your hello's `game`, `client`, `foundation`, `integration` and `v` — and since when (the
`integration` is the one Astra registered for your token once it manages per-profile tokens, never
the hello's own). Never a frame
counter, never anything you send per frame. The game's name stays on Astra's own authenticated
channel: it does not reach Astra's plugins or its AI. While the owner is asked about your game
(§Consent below) the status says so — `asking`, with the same fields — and Astra shows him the
prompt. (`docs/game-bridge-prod-readiness.md` §4 is the full design.)

## The frame ring (shared memory)

"MCPT" v1 — byte-compatible with the format of the Minecraft-in-Death-Stranding frame exporter, so
a reader for one reads the other. Little-endian.

```text
header (4096 B)   @0  u32 magic 0x5450434D ('M','C','P','T')   @4  u32 version 1
                  @8  u32 header bytes (4096)                   @12 u32 slots (6)
                  @16 i64 slot stride                           @24 u32 max w   @28 u32 max h
                  @32 i64 publish counter (bumped LAST)         @40 i32 latest slot (-1 = none)
                  @44 i32 producer pid
slot i @ 256+128i +0  i64 seq (ODD while being written)         +8  i64 engine frame
                  +16 i64 host frame (= your cam.id)            +24 u32 w   +28 u32 h
                  +32 f32 near   +36 f32 far (informational)    +40 f32 vfov (degrees)
                  +44 u32 flags                                 +48/+56/+64 f64 cam pos (verbatim)
                  +72/+76/+80 f32 echo (verbatim)               +84 i32 VIEW (your cam.view; 0 before)
                  +88 i64 capture ns  +96 i64 publish ns        +104 i64 publish number (below)
                  +112 u32 where her SHADOW CASTER starts, from the slot's data (0 = none)
                  +116 u16 x  +118 u16 y  +120 u16 width  +122 u16 height: her RECTANGLE of the
                                 w×h picture (flag 128 only; 0 otherwise), rows top-down
                  +124 u32 where her BONES start, from the slot's data (0 = none)
data @ 4096 + i·stride, planes tight at the CURRENT w,h — under flag 128 at the RECTANGLE's size:
                  colour  w·h·4  RGBA8, PREMULTIPLIED alpha, display-encoded (sRGB) — what the
                                 desktop window shows; ready for `out = colour + game·(1 − a)`
                  depth   w·h·4  f32 LINEAR eye-space metres along the view axis; 0 = nothing
                  plane 3 @ 2·w·h·4 (flag 128: 2·width·height·4 of the rectangle): unused
                                 (flag 16) — or, under flag 32, her SUN VIEW:
sun view (flag 32) +0 u32 'SUNV' (0x564E5553)   +4 u32 N (512)
                  +8/+16/+24 f64 centre (world, glTF)          +32 f32 right[3]  +44 f32 up[3]
                  +56 f32 dir[3] (the way the light TRAVELS)   +68 f32 half-extent (m)  +72 f32 near
                  +76..128 zero        then N×N f32 @ +128: metres along dir from the near plane
                  (the plane at centre − dir·near); 0 = not her. Rows top-down, row 0 at +up,
                  columns along +right.
caster (flag 64)  at slot data + (+112), 16-aligned after the sun view (or after the picture):
                  +0 u32 'CAST' (0x54534143)   +4 u32 V vertices (≤ 8192)   +8 u32 T triangles
                  +12 zero   +16 u64 topology id   +24/+32/+40 f64 origin (world, glTF)  +48..64 zero
                  then V × f32[3] positions @ +64: game metres from the origin, world axes
                  then V × f32[3] unit normals, world axes
                  then T × u16[3] triangles, counter-clockwise seen from the outside (glTF)
bones (flag 256)  at slot data + (+124), 16-aligned after the caster (or where it would start):
                  +0 u32 'BONE' (0x454E4F42)   +4 u32 N (≤ 8)   +8 u32 REV (the reply's bones_rev)
                  +12 u32 zero
                  +16/+24/+32 f64 origin (world, glTF — the caster's)   +40..48 zero
                  then N × 32 B @ +48:  +0 u16 index into YOUR hello.bones  +2 u16 flags (1 = valid)
                                        +4 f32 pos[3]: game metres from the origin, world axes
                                        +16 f32 rot[4]: x, y, z, w — world axes
```

- **Flags** the engine writes: `8` (depth is linear metres) | `16` (plane 3 unused) or `32`
  (plane 3 holds her sun view) | `64` (the slot carries her shadow caster) | `256` (the slot carries
  the pose of the bones your `hello.bones` named) | `128` (the planes are her
  rectangle only — `hello.crop`; an EMPTY rectangle means nothing of her is in this view: clear her,
  as for an all-zero picture; outside the rectangle she is transparent). Rows are
  **top-down** (flag `2` — bottom-up — is never set). Readers of the Minecraft format should honour
  all flags: `1` = depth NDC `[0,1]`, `2` = bottom-up rows, `4` = reversed Z.
- **Reading** (a seqlock): read the counter @32; unchanged → no new frame. `slot` = @40. Read `seq`;
  odd → try later. Copy the planes and the descriptor. Read `seq` again; changed → discard.
  **Re-read the stride @16 with every frame**: if it is not the one you mapped by, the ring was
  re-made larger (a `hello.want`) — drop the frame and map the ring again from its header (the
  seqlock alone cannot tell you: a frame read at the old stride passes it).
- **Reading one VIEW** (only when you send `view`s): the latest slot @40 is the newest frame of ANY
  view — after view 0's frame the engine publishes the mirror's, so @40 usually points at the
  mirror. To read view `k`, scan all `slots` descriptors and take the slot whose `view` (+84) is `k`
  with the LARGEST publish number (+104 — the counter value that frame was announced with; it keeps
  counting across game switches, unlike the engine frame at +8, and across an engine restart for as
  long as the ring's memory lives; 0 = a slot never written — never read one, on Windows its pages
  may not even be committed), then read it with the same seqlock. Match its `host frame` to a `cam.id` you sent for
  that view — a slot of a view you stopped sending is old. The six slots keep the newest frame of
  each of up to six views that send every game frame; with seven or eight such views an older
  view's frame may already be overwritten when you look (keep it to six, or read right after you
  send). A game that never sends `view` sees exactly the old ring: only slots 0–2 are used and +84
  is always 0, so a reader of the latest slot is unaffected.
- **Depth** is the NEAREST of the MSAA samples, never an average, so her silhouette does not halo
  in front of walls. Where alpha is 0 the depth means nothing (it is 0).
- A frame with nothing to show (no body loaded yet) is published as all zeros: **clear** her, do
  not keep the last picture.
- **The ring is made when your game claims her** — before the `hello` reply names it — sized for
  your `hello.want` (1920x1080 without one; `maxW`/`maxH` in the reply), and let go when she leaves
  the game: on Linux its slots' pages are freed (your mapping then reads zeros there; the header
  stays) and the FILE is never removed, not even at the engine's clean exit, so a restarted engine
  adopts it — its header, its size if larger, and its publish counter; on Windows the engine drops
  its view (the section lives while you hold it) and commits the whole ring the moment it makes it.
  A later `hello` asking for a LARGER picture re-makes it before its reply (same name; the publish
  counter carries on); it never shrinks while the engine runs, and on Linux the file keeps its
  largest size until it is removed or the machine restarts. So **map the ring at every `hello`**
  (the reply's `shm`) and keep that mapping until your next one; after a reconnect map it again,
  re-read the header and reset the counter you last saw (a Windows section made afresh counts from 0).
- **What a size costs.** The ring is `4096 + 6 · stride` bytes, `stride = 3 · w · h · 4`. Windows
  commits all of it (pagefile-backed) while either side holds it; on Linux the file is sparse, and
  what costs RAM are the pages written — colour + depth at each picture's size (`2 · w · h · 4`, or
  only her rectangle under `crop`) in the 3 slots a one-camera game cycles through (6 with views).
  The engine's own GPU readback buffers are 3 more pictures' worth (6 with views), of the largest
  size asked for since she came into a game: they never shrink while she is there — held until
  the hosting session ends (games-only: until the process exits; from the desktop: until she
  returns to it).

  | `want` | ring (Windows commit) | Linux pages, 3 whole pictures | readback (3 slots) |
  |---|---|---|---|
  | 1920x1080 | 142.4 MiB | 47.5 MiB | 50.5 MiB |
  | 2560x1440 | 253.1 MiB | 84.4 MiB | 87.4 MiB |
  | 3840x2160 | 569.5 MiB | 189.8 MiB | 192.8 MiB |
  Size the mapping from the header (`4096 + slots · stride`), never from a constant: a ring made by
  an engine before views had 3 slots, one made since has 6. (On Windows a game still holding an
  older 3-slot ring when the engine restarts keeps it: the engine then publishes every view through
  those 3.) An engine before 0.2.0 made the ring at its start and never removed it.
- **The caster rides with the PRIMARY view's frames only** (view 0 once one arrived): every view of a
  wake is drawn at one pose, so it is written once — read it from view 0's newest frame. It is
  written only when your `hello` asked for it (`"shadow":"caster"` or `"both"`).
- **The bones ride with EVERY view's frames** (unlike the caster), each in the pose of the picture
  beside it: read them from the slot you composite. Written only when your `hello` named `bones`
  and the frame has a picture of her (no body, a 2D character: no flag 256 and `+124` = 0 — hide
  your item then). N is the number of bones the reply's `bones` listed; each entry's index is the
  bone's place in YOUR `hello.bones` (a name the engine did not know leaves a gap), in the reply's
  order. REV is the revision of the hello list its indices index: compare it with the last reply's
  `bones_rev` and ignore a section that differs. A bone her body lacks (a VRM 0.x rig without `upperChest`, say) comes with flags 0 — its
  position and rotation mean nothing (so does one whose pose could not be worked out: the bone is
  flagged invalid, never published as NaN). Positions are her SOLVED pose of that frame (after every
  animation, IK, look-at and spring step), placed exactly as her picture is: `pos`, facing, `up`,
  `scale` and the eased `anchor` applied. The rotation is the bone's turn FROM HER REST POSE: at
  rest (her T-pose) every bone's axes are her body's own — +Y up, +Z her facing, +X her left —
  turned with her into your world, whatever rig the body has, so an offset you tune on one
  character holds on another. **Capacity:** a section is at most 48 + 8 × 32 = 304 bytes, written
  in plane 3 behind the sun view and the caster (16-aligned), and left out — flag 256 off, `+124` =
  0 — whenever it does not fit what is left of the slot: a ring whose plane 3 the sun view or the
  caster fills to within 352 bytes of its end drops the bones (they are the last to be placed, so
  the first to go), and so does a ring too small to have the room at all. The engine's default
  ring holds the sun view, the largest caster and eight bones; read flag 256 on every slot, never
  assume it. A view whose crop rectangle is empty (nothing of her in it) still carries the bones,
  and a game shows no item from it.
- `host frame` + `cam pos` + `echo` say exactly which of your cameras the picture was rendered
  from: reproject it to your current camera to hide the one-frame latency.

## Compositing tips

- **Timing decides whether she sits IN your world or swims over it.** Take your camera and her
  placement at the START of your frame — before your post-processing and before you composite her —
  and send them right then, the frame's `avatar` (and anything else for that frame) FIRST and its
  `cam` LAST: the `cam` commits the frame and the engine draws at once, so a message written after
  it lands on the next frame. The picture for a `cam` is in the ring about 4–5 ms after it (p50; p99
  ~7 ms — measured 2026-10-06 at 1920×1080 on a busy desktop), so it is usually ready by the time you
  present; a hook that has the camera EARLIER — your game-update step, before your render thread
  draws the frame — gives the engine a whole frame instead. Then composite with
  THIS frame's camera: reproject the newest picture from the camera it was drawn from (`cam pos` +
  `echo` in its descriptor) to the camera you are presenting. Reading the camera AFTER compositing
  (e.g. in a ReShade `reshade_present` handler, which runs after the effects) makes the warp a no-op
  and leaves her one frame behind the world — a ghost on every fast turn (seen in Death Stranding,
  2026-10-05).
- Depth-test both ways: show her pixel where `her_depth < your_depth + bias` (a few cm).
- She is already lit (MToon, her own key light and shadows). Do not relight her as if she were
  unlit albedo — send `light` instead and the engine lights her with your sun, sky and lamps.
- **Her shadow on your ground** comes from your side, and the engine gives you what it needs: while
  a `light` with a sun above the horizon (and `intensity·visible > 0`) lights her, every frame
  carries her VIEW FROM THE SUN in plane 3 (flag 32) — an orthographic depth render of her from
  that sun, her own self-shadow map reduced to 512² (the NEAREST surface per texel, so hair and
  fingers survive), in your world's metres with her scale applied. For a ground point `P`:
  `u = (P − centre)·right / half`, `v = (P − centre)·up / half`, `t = (P − centre)·dir + near`; it is
  in her shadow where the texel at `(u, v)` is non-zero and less than `t` (minus a bias). The basis
  is the one that frame was rendered with.
- **Better: let your own renderer cast her shadow.** Every frame with a picture of her also carries
  her SHADOW CASTER (flag 64): a coarse copy of her body (a few thousand triangles, built from her
  own mesh by vertex clustering, ~2 cm) in exactly the pose of that frame's picture, placed in your
  world. Put it in your scene as an object that casts shadows and is never drawn (Unity:
  `ShadowCastingMode.ShadowsOnly`), and she is shadowed by EVERY light you have — your sun, a
  flashlight, a lamp — with your shadow filtering, cascades and post-processing, in every camera
  and mirror of yours. Positions are relative to `origin` (`f64`, so a world kilometres wide costs
  no precision) and in your world's axes after the usual z flip of a left-handed engine — which
  also reverses the winding: flip each triangle, or draw both sides (her hair and skirt are single
  sheets; drawing both sides is the safer choice for a shadow). Upload the triangles only when
  `topology` changes (another character, another body); it is the same for the same triangles in
  any engine process, so keeping it across an engine restart is right. A frame without a caster
  (no body, a 2D character, a ring configured too small to hold it) has no flag 64 and `+112` = 0:
  hide your copy then. Expressions are not in it, nor are her accessories yet.

- **An item in her hand** (`hello.bones`): each frame, put your item at `origin + pos` turned by
  `rot` from the bone's entry IN THE SLOT YOU COMPOSITE — never the newest slot of another view, and
  never last frame's — then apply YOUR grip offset (a position and a rotation in the bone's frame:
  the game's own numbers, tuned once for the item, e.g. a sword's handle across her palm). Convert
  like the caster: the same z flip of a left-handed engine applies to `pos`, and to `rot` (for a z flip
  the quaternion `[x, y, z, w]` becomes `[−x, −y, z, w]`). Hide the item while the
  slot has no flag 256 or the entry's valid flag is 0. Draw it in your scene so your depth, light and
  shadows treat it as your own; her picture's depth then sorts it against her hand.

## Security

The bridge listens on loopback only, refuses browsers, serves at most 8 sockets (past that the
oldest one that has not claimed her and is at least a second old is closed to make room — one
holding her never is; with none that old, the new socket is closed), and hands her to
ONE game: the slot is claimed by the first authorised `hello` (with no token, the first message) —
never by a bare connection, so a silent socket cannot lock your mod out — and released when that
game closes or sends nothing for 10 s, or the owner turns games off. Holding the slot does not take
her off the desktop: the game's first `cam` does. A connection that has not claimed it within 5 s is closed.
Messages are capped at 64 KiB. A second engine on the same port fails at start, before it touches
the ring. The bridge translates a game's messages only into presentation commands: a game can place and frame her,
never load files, change her character, reach Astra's daemon or read anything back but frames and
her state. The one exception is opt-in per game: with capture switched ON by the owner for its
integration, a game may offer a `.vrma` it wrote into ITS capture inbox (`<user root>/inbox/
capture/<integration>/`, created `0700`); the engine copies that one regular file (never a link,
never outside the inbox, at most 32 MiB, parsed first) into the owner's own pack for that game. The ring file is created `0600` and a planted symlink or another user's file is never written into (the ring is made at `<name>-<uid>` beside it, which the `hello` reply names).

**Tokens: one per game profile, once Astra says so** (engine 0.2.8+; plan A §3.4 of the
game-integrations Workshop). Astra gives each game profile its own bridge token (the launcher's
`${bridge_token}`) and sends the engine the map `sha256(token) → (integration, profile)` over its
own authenticated channel (`CompanionCommand.set_bridge_tokens = 25`), re-sent whenever it
reconnects; the engine says it understands this with the capability
`supports_bridge_token_sets=true` (the key is new, not a rename of `supports_bridge_tokens`: the
engine that shipped field 25 reported that older key WITHOUT decoding `per_profile` below, so an
`{per_profile: false, tokens: []}` meant as "not managed yet" arrived there as an empty managed
set and refused every game — the daemon cannot tell that engine apart from one that understands
`per_profile`, so the capability moved to a key only the latter reports, and the old key is no
longer sent at all), and Astra sends a MANAGED message (`per_profile: true`) to no engine that does not. The message
carries `per_profile` (field 2) alongside the map: Astra cannot mint per-profile tokens yet, so
until it can, it sends `{per_profile: false}` — the engine's old rule stands and `tokens` is
ignored. `per_profile` defaults to `false` (the proto3 default), so an older Astra, or one that
never sends the message at all, means exactly the same thing. Once a `per_profile: true` message
has arrived the engine is MANAGED for the rest of the process; a later `per_profile: false` is
ignored (one warn line) — once managed, managed until the engine restarts. Then, once managed:

- a `hello` is let in only when the sha256 of its `token` is in the map; otherwise it is answered
  `error` with a sentence you can show the player ("start the game from Astra (Games)…") and may
  retry. A refused hello on a connection that already HOLDS her (a re-hello) lets her go: the
  `error`, then the socket closes;
- a refused re-hello on a connection that holds her closes the session in EVERY mode — the old
  install-wide-token rule below included (before 0.2.8 it kept her and refused your commands until
  you went quiet);
- a re-hello with ANOTHER registered token switches the session to that token's integration (and
  Astra's status follows it); her game packs follow at most once a second per connection, the
  latest integration winning;
- **your session's integration is the map's** — `hello.integration` is ignored (a disagreement is
  logged) — and everything keyed by integration follows it: the capture inbox, the stored
  `params` declaration, the game packs, the source your parameters are kept under;
- an EMPTY map in a `per_profile: true` message means "Astra manages tokens and none is valid":
  every `hello` is refused (never confuse this with `per_profile: false`, which means Astra does
  not manage tokens yet — the old rule, not a refusal);
- a token Astra revokes (an uninstall, a revoked profile) drops a game holding her AT ONCE — within
  a read tick (50 ms): an `error` saying why, the socket closed, her slot released. A game let in
  by the old install-wide token, or with none, is dropped the same way when the first map arrives;
- a token keeps its game as long as the map lists it for the SAME integration — renaming its
  profile changes nothing;
- tokens are compared in constant time (as digests), and neither a token nor its digest is ever
  logged — at most an 8-hex-digit prefix of the digest.

**Under Astra, until its first map arrives**, every `hello` is answered `error` "Astra is
starting" (retry quietly, as for the owner's switch) — for at most 5 s: past that with no map (an
Astra too old to send one) the old rule below takes over for that run.

With no map ever received — a standalone engine, or an Astra too old to send one — the old rule
stands exactly: the install-wide `ASTRA_BRIDGE_TOKEN` / `WGPU_BRIDGE_TOKEN` (or none), and your
hello's `integration` as you sent it. Once a map has arrived the engine never returns to that rule
for the rest of its run, even if Astra goes away.

### Consent: the owner is asked about a game that has no token

**Engine 0.2.8+, under an Astra that sends it** (`CompanionCommand.set_game_consent = 27`, capability
`supports_game_consent=true`). A game Astra starts itself carries a token and is never asked
about. A game that reaches her WITHOUT one — no per-profile token, no install-wide token — is let in
only with the owner's yes, once Astra says he decides. Astra sends the engine his answers as a table
of `client → allow / no`, keyed by your hello's **`client`** exactly as the engine shows it (after
it drops control and bidi characters and keeps 64 characters) and case-sensitively — so keep your
`client` stable. Then, for a tokenless hello:

- **a `client` the owner allowed** is let in as before;
- **one he refused** is answered `error` `Astra's owner said no to this game`;
- **one he has not answered** is answered `error` `waiting for the owner to allow <game> in Astra`
  (`<game>` is your hello's `game`, else the part of `client` after its last `/`) while Astra shows
  him the prompt. **Keep retrying your hello every couple of seconds, quietly** — the retry after he
  allows it is let in, nothing else is needed. One game is asked about at a time: another unknown
  game is answered `another game is waiting for the owner's answer` meanwhile, and while another
  game holds Astra (even one that has not sent its first `cam`) it is answered `another game
  already holds Astra` and he is not asked. The question lapses 10 s after your LAST hello (a mod
  that gave up stops asking him), and ends at once when his no reaches your retry, or when his yes
  lets your retry claim her;
- **a hello with an empty `client`** — or a bare command with no hello at all — is answered `this
  game did not say which game it is` and never asked about: always send `hello` with a `client`.

If the owner takes back his yes (or turns it into a no) while your game holds her, she leaves it at
once, as for a revoked token — the `error` `Astra's owner has not allowed this game` (or `…said no
to this game`), then the socket closes. A game a token admitted is never asked, and never dropped
by this table. A standalone engine, or an Astra that never sends the table, asks nobody: tokenless
games are let in exactly as before. Astra re-sends the table whenever it reconnects; the engine
never writes it to disk.
