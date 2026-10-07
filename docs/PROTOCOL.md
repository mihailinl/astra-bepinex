<!-- A copy of BRIDGE.md from astra-avatar-engine (the engine that implements this protocol).
     The engine's copy is the source of truth; this one follows it. -->

# The Astra game bridge — putting Astra into any game

The engine can run with **no window** and draw Astra into a game instead: a game mod sends the
engine the game's camera and where she stands, and the engine publishes her picture — colour plus
per-pixel depth — into shared memory for the mod to composite into the game with a depth test, so
the game's walls hide her and she hides the game.

The game owns the WORLD (collision, navigation, where she is). The engine owns HER (pose, animation,
face, voice). This page is the contract a mod is written against. The design and the roadmap are in
`notes/game-bridge.md`; the example client is [`tools/bridge/fake_game.py`](tools/bridge/fake_game.py)
(standard-library Python, no game needed).

> **Phase 1 (2026-10-04):** picture, camera, placement, frame ring. **Phase 2a:** animation by name
> (`play` / `trigger` / `resume`), the placement `anchor`, and a CHAIR state (`seat`) for vehicles.
> **Phase 3:** animator PARAMETERS — send her `speed` and `airborne` and she walks, jogs, sprints
> and jumps, at a pace matched to yours. Mood, look-at and events back follow.

## Running it

**Nothing to run, usually:** the desktop pet listens on the bridge's port, and a game that connects
there and says `hello` takes her. The reply is the ordinary `hello`, the connection stays open, and
she leaves the desktop within a frame or two: her window is closed, so nothing is left drawing over
your game, and the same process draws her into it. A `cam` or `avatar` sent in those first
milliseconds may be ignored; the next frame's is not. When your game closes, crashes or goes quiet
and does not come back within 8 s, she returns to the desktop, with the Studio reopened if
it was open. `WGPU_GAME_SWITCH=off` keeps her on the desktop and opens no port. To run
game mode directly (and stay in it):

```bash
WGPU_PROTO_WINDOW=bridge astra-avatar-engine
```

| var | default | meaning |
|---|---|---|
| `WGPU_PROTO_WINDOW=bridge` | — | **The switch.** No window; serve a game instead. Works on Linux, Windows and macOS. |
| `WGPU_BRIDGE_PORT` | `25600` | WebSocket port, on `127.0.0.1` only. |
| `WGPU_BRIDGE_SHM` | `astra-frame` | Name of the frame ring: `/dev/shm/<name>` on Linux (Wine/Proton sees `Z:\dev\shm\<name>`), `Local\<name>` on Windows, `$TMPDIR/<name>` on macOS. `[A-Za-z0-9._-]`, never a path. |
| `WGPU_BRIDGE_MAX` | `1920x1080` | Largest picture the ring carries. A bigger camera request is scaled down, aspect kept. |
| `WGPU_BRIDGE_TOKEN` | — | Optional shared secret: when set, nothing but `hello` is honoured until a `hello` carrying `"token"` arrives. |

A default (daemon) build is auth-gated by Astra like the desktop pet; a `--no-default-features`
build runs standalone.

**The Companion Studio beside the game:** a build with the `debug-ui` feature (`cargo build
--release --no-default-features --features debug-ui` for a standalone one) opens the Studio in its
own window when started with `WGPU_PANEL=on`. It edits the Astra in the game live — states, clips,
parameters, lighting — and never appears in the game's picture. There is no F1 inside a game:
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
{"t":"hello","v":1,"client":"my-mod"}
{"t":"cam","id":1234,"pos":[x,y,z],"fwd":[x,y,z],"up":[x,y,z],"fovY":45.75,"w":1920,"h":1080,"echo":[a,b,c]}
{"t":"avatar","id":0,"pos":[x,y,z],"fwd":[x,y,z],"up":[x,y,z],"vel":[x,y,z],"anchor":"hips","scale":1.2,"params":{"speed":1.4,"airborne":false}}
{"t":"param","set":{"speed":0.0}}          // parameters on their own (null = back to the default)
{"t":"play","state":"seat"}               // enter a state of her animation set by name
{"t":"trigger","name":"music_mid"}         // raise a token into her graph
{"t":"resume"}                             // hand her back to the graph's default state
{"t":"light","sun":{"dir":[x,y,z],"color":[r,g,b],"intensity":1.0,"visible":1.0},"ambient":[r,g,b]}
```

- **`cam`** — once per game frame. `pos` is the eye; it looks along `fwd`; `up` fixes the roll.
  `fovY` is the VERTICAL field of view in degrees (1–179); the projection is a plain symmetric
  perspective at `w/h` aspect, no jitter. `w`/`h` is the picture size you want (keep your aspect;
  ~1080p is plenty). `id` is your frame id and comes back in the frame. `echo` (≤ 8 numbers, the
  first 3 kept) is copied into the frame verbatim and never interpreted — put whatever your
  reprojection needs there. **A frame is drawn for each `cam`**; several since the last draw
  collapse into the newest.
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
  the animation set you expect her to wear — a cue for another set is declined. The built-in set's
  states: `idle`, `sit` (a window-edge perch — not for games), `drag`, **`seat`** (a chair: sits
  down, sits, stands up; `trigger unseat` stands her up through the outro, `resume` at once),
  `dance_slow` / `dance_mid` / `dance_fast`, and **`move`** / **`air`** (below) — which you never
  `play`: her parameters take her there.
- **`params`** (on `avatar`, or alone as `param`) are her animation set's ANIMATOR PARAMETERS —
  **raw facts, never decisions**: send how fast she moves, not `gait: "run"`. What a fact means —
  which state, which clips, how fast her legs go — is decided by her animation set, which a user can
  edit or replace (a set could declare `altitude` and author flying). A number, a bool (1/0), or
  `null` to hand it back to the set's default. A name the set does not declare is ignored (the
  engine says so once). The built-in set declares two:

  | parameter | type | what to send |
  |---|---|---|
  | `speed` | float, body-relative | her ground speed in YOUR m/s (the length of `vel` along the ground is right); read at her `scale` |
  | `airborne` | bool | she is off the ground — a jump or a fall. Debounce it: a flag that flickers for a frame on rough ground makes her hop. |

  Above `speed` 0.15 she is in **`move`**: one blend over walk (0.7 m/s) → jog (2.4) → sprint (4.0),
  her legs paced to the speed you send so her feet do not skate (up to 1.5× past the last). Below
  0.08 she stands. `airborne` puts her in **`air`**; landing while still moving goes straight back
  into her stride, landing standing plays the landing. The clips are IN PLACE: you move her (`pos`),
  the clip moves her legs.
- **`light`** — the light of your world around her. `sun.dir` is the unit direction TOWARD the
  sun (glTF world; it is turned into her frame, so a tilted vehicle tilts it across her); `color`
  linear RGB, normalised; `intensity` against her own key light's budget (1 ≈ a daytime sun, 0..16);
  `visible` 0..1 is how much of her the sun reaches — YOUR occlusion (a rock, a roof), which her own
  self-shadow cannot see. `ambient` is the linear sky/environment colour her shadowed side becomes.
  Either part may be left out (hers is kept); `{"t":"light"}` with neither hands her back her own
  light. It replaces her key light for the shading AND her self-shadow (hair on her face follows
  your sun). It holds until the next `light` — send it on change or at ~10 Hz.
- **When your game lets go of her** — it closes, crashes, or sends nothing for 10 s — every
  parameter it set returns to its default, if it cued her graph she returns to its default state,
  and if it lit her she gets her own light back. The next game finds her standing.
- A message that fails validation (a non-finite number, a zero `fwd`, a fov out of range, a size
  over 8192…) is refused with an `error` naming the field; the previous camera stays in force.

### Engine → game

```json
{"t":"hello","v":1,"engine":"astra-avatar-engine","ver":"0.1.45","shm":"/dev/shm/astra-frame","maxW":1920,"maxH":1080}
{"t":"error","msg":"\"fovY\" 0.5 is outside 1..=179 degrees"}
```

Map the ring named by `shm` in the hello reply.

## The frame ring (shared memory)

"MCPT" v1 — byte-compatible with the format of the Minecraft-in-Death-Stranding frame exporter, so
a reader for one reads the other. Little-endian.

```text
header (4096 B)   @0  u32 magic 0x5450434D ('M','C','P','T')   @4  u32 version 1
                  @8  u32 header bytes (4096)                   @12 u32 slots (3)
                  @16 i64 slot stride                           @24 u32 max w   @28 u32 max h
                  @32 i64 publish counter (bumped LAST)         @40 i32 latest slot (-1 = none)
                  @44 i32 producer pid
slot i @ 256+128i +0  i64 seq (ODD while being written)         +8  i64 engine frame
                  +16 i64 host frame (= your cam.id)            +24 u32 w   +28 u32 h
                  +32 f32 near   +36 f32 far (informational)    +40 f32 vfov (degrees)
                  +44 u32 flags                                 +48/+56/+64 f64 cam pos (verbatim)
                  +72/+76/+80 f32 echo (verbatim)               +88 i64 capture ns  +96 i64 publish ns
data @ 4096 + i·stride, planes tight at the CURRENT w,h:
                  colour  w·h·4  RGBA8, PREMULTIPLIED alpha, display-encoded (sRGB) — what the
                                 desktop window shows; ready for `out = colour + game·(1 − a)`
                  depth   w·h·4  f32 LINEAR eye-space metres along the view axis; 0 = nothing
                  plane 3 @ 2·w·h·4: unused (flag 16) — or, under flag 32, her SUN VIEW:
sun view (flag 32) +0 u32 'SUNV' (0x564E5553)   +4 u32 N (512)
                  +8/+16/+24 f64 centre (world, glTF)          +32 f32 right[3]  +44 f32 up[3]
                  +56 f32 dir[3] (the way the light TRAVELS)   +68 f32 half-extent (m)  +72 f32 near
                  +76..128 zero        then N×N f32 @ +128: metres along dir from the near plane
                  (the plane at centre − dir·near); 0 = not her. Rows top-down, row 0 at +up,
                  columns along +right.
```

- **Flags** the engine writes: `8` (depth is linear metres) | `16` (plane 3 unused) or `32`
  (plane 3 holds her sun view). Rows are
  **top-down** (flag `2` — bottom-up — is never set). Readers of the Minecraft format should honour
  all flags: `1` = depth NDC `[0,1]`, `2` = bottom-up rows, `4` = reversed Z.
- **Reading** (a seqlock): read the counter @32; unchanged → no new frame. `slot` = @40. Read `seq`;
  odd → try later. Copy the planes and the descriptor. Read `seq` again; changed → discard.
- **Depth** is the NEAREST of the MSAA samples, never an average, so her silhouette does not halo
  in front of walls. Where alpha is 0 the depth means nothing (it is 0).
- A frame with nothing to show (no body loaded yet) is published as all zeros: **clear** her, do
  not keep the last picture.
- The ring is created once, sized for `max`, and **never shrunk or removed** — map it once and keep
  the mapping.
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
  unlit albedo — send `light` instead and the engine lights her with your sun and sky.
- **Her shadow on your ground** comes from your side, and the engine gives you what it needs: while
  a `light` with a sun above the horizon (and `intensity·visible > 0`) lights her, every frame
  carries her VIEW FROM THE SUN in plane 3 (flag 32) — an orthographic depth render of her from
  that sun, her own self-shadow map reduced to 512² (the NEAREST surface per texel, so hair and
  fingers survive), in your world's metres with her scale applied. For a ground point `P`:
  `u = (P − centre)·right / half`, `v = (P − centre)·up / half`, `t = (P − centre)·dir + near`; it is
  in her shadow where the texel at `(u, v)` is non-zero and less than `t` (minus a bias). The basis
  is the one that frame was rendered with.

## Security

The bridge listens on loopback only, refuses browsers, serves at most 8 sockets, and hands her to
ONE game: the slot is claimed by the first authorised `hello` (with no token, the first message) —
never by a bare connection, so a silent socket cannot lock your mod out — and released when that
game closes or sends nothing for 10 s. A connection that has not claimed it within 5 s is closed.
Messages are capped at 64 KiB. A second engine on the same port fails at start, before it touches
the ring. The bridge translates a game's messages only into presentation commands: a game can place and frame her,
never load files, change her character, reach Astra's daemon or read anything back but frames and
her state. The ring file is created `0600` and a planted symlink or another user's file is refused.
