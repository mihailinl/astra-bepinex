# Where a Unity game can lose Astra — and what the foundation does about it

A workflow over the owner's installed Unity games (2026-10-09): survey, five lenses, a plan, seven fixes (0.6.0), an adversarial review. This is its plan as written, kept as the map of known cases; the follow-ups at the end are OPEN unless marked DONE. 2026-10-14: the depth-writing pass (case 6, SHADER (1)) and the 2020 line are done — see the follow-ups.

# Astra foundation: render and IL2CPP plan (2026-10-09)

## Answers to the owner's three questions

1. **ULTRAKILL "through walls"**
   - It is not URP. ULTRAKILL uses the Built-in pipeline.
   - It is not specific to ULTRAKILL either. It is a general Built-in mechanism:
     - Today she is tested against `_CameraDepthTexture`.
     - In forward rendering, Unity fills that texture by drawing every opaque object a second time with its shader's **ShadowCaster** pass.
     - ULTRAKILL's world shader `ULTRAKILL/Master` has no ShadowCaster pass and no Fallback (two lenses parsed the bundle). Unity's own `Unlit/*` shaders have none either.
     - So any Built-in game built from unlit, retro or custom shaders has the same hole.
   - The walls are in the camera's depth **buffer**. The fix is in the foundation: draw her inside the camera and test her against that buffer (T2).
   - The survey blamed portals and skyboxes. That explanation is wrong; they are a secondary effect at most.
2. **"Over particles"**
   - This is universal. Particles never write depth, and she is drawn after them.
   - Only a shader change fixes it: a pass that writes her depth, drawn before the transparents. That is a follow-up (DONE 2026-10-14: SHADER (1)).
   - The 2022.3.62f2 editor **is** installed (`/mnt/data/Unity/Hub/Editor/2022.3.62f2`, 7.5 GB, Windows and Linux standalone support), so the next run can build that bundle.
3. **Schedule I**
   - It does not need MelonLoader. BepInEx be.788 loaded the plugin fine; our `Start` threw.
   - The cause: this IL2CPP build kept no body for `RenderPipelineManager.add_endContextRendering`, because the game never subscribes to that event.
   - The fix is general (T1): when an accessor was stripped, subscribe through the event's backing field. The foundation already does this for `Camera.onPreCull`.
   - MelonLoader uses the same Il2CppInterop and would hit the same gap.
   - Two IL2CPP failures happen **before** our plugin loads, so no C# in the foundation can fix them. Both belong to the launcher (follow-ups):
     - Unity 6000.3 and later: the loader crashes the game at boot.
     - Unity 2021.3.46+ and 2022.3.63+: Unity base libraries return 404, so no interop assemblies are generated.

## Findings verified against the code (and corrections)

Confirmed:
- `Compat.cs:57`: the catch list leaves out `NotSupportedException`. `Compat.Stripped` (`:289`) already covers it but is not used there. `HookContextEnd` (`:65`) has no `[NoInlining]`. `HookSrp` (`:24-43`) has no guard at all.
- `Driver.cs:103`: one `Fenced("start")` around everything.
- `Driver.cs:170`: a faulted plugin never opens the link.
- Schedule I's interop:
  - `add_`/`remove_` for `endContextRendering` and `endFrameRendering` are `throw new NotSupportedException("Method unstripping failed")`.
  - The static field properties `begin/endCameraRendering` and `endContextRendering` (`NativeFieldInfoPtr_*`) are present.
  - `EndContextRendering(ctx, cams)` is native.
  - The 6000.3 testbed interop that we compile against has the same `get_/set_` properties.
  - MiSide's interop lacks `add_begin/endCameraRendering` altogether, so any IL2CPP SRP game shaped like it would also die in the unguarded `HookSrp`.
- `Compositor.cs:365-387`, `PrepareBuiltIn`:
  - It sets `depthTextureMode |= Depth`, appends at `AfterImageEffects`, and draws pass 0 into `CurrentActive`.
  - Pass 1 `CompositeZ` (`ZTest LEqual`, `ZWrite Off`, `SV_Depth`) exists and works for Built-in: `_ProjectionParams` handles the flip, and `GL.GetGPUProjectionMatrix` gives the z, reversed or oblique.
- ULTRAKILL `ppv2.cs`:
  - `SetTargetBuffers(MRT, depthBuffer)` (`:404`).
  - `RemoveCommandBuffers(AfterForwardAlpha)` on re-init (`:405`).
  - `HeatWaves` at `AfterForwardAlpha` does `SetRenderTarget(null,null)` and then a colour-only `Blit` (`:454-458`).
  - `bloodOilCB` at `BeforeForwardAlpha`, `outlineCB` at `AfterEverything`.
  - Nothing at `BeforeImageEffects` or `AfterImageEffects`.
- `DrawNow` (`:280-282`):
  - When nothing was kept, it tests against whatever `_CameraDepthTexture` is bound at the context end.
  - URP 14 binds `Texture2D.black/whiteTexture` as a placeholder (`UniversalRenderer.cs:984-987`), which passes the `!= null` check.
  - The `"walls hide her"` log line then lies.
- `RequestDepthTexture` and `IsUrpOverlay` are `#if !IL2CPP`.
  - Schedule I's `UniversalAdditionalCameraData` has a native `requiresDepthTexture` and `renderType`, plus the field-backed `m_RequiresDepthTextureOption`, `m_CameraType` and `m_Cameras`.
- `Camera.GetCommandBuffers`, `RemoveCommandBuffers`, `commandBufferCount` and `AddCommandBuffer` are restored icall bodies in both IL2CPP interops.
- `Follower.cs:223`: while there is no player, the search runs every frame.
- `Urp14.cs:61-67`: the comment claims she writes depth. She does not. (Since 2026-10-14 she does: pass 2.)

Corrected or rejected:
- **Rejected:** "make the bridge connect without the hooks" (survey). It breaks B1, the rule that the foundation must never hold her when it cannot draw her. The link is already opened lazily in `LateUpdate`. The right fix is hooks that cannot throw.
- **Rejected:** "fall back to `endCameraRendering`" for the context end. The final blit of a URP stack covers her. The field route keeps the context end.
- **Corrected:** the Built-in draw point. `BeforeImageEffects` was proposed, but after `HeatWaves` the active target there may be colour-only. The choice is `AfterForwardAlpha`, with her buffer kept **first**: the active target there is what Unity just drew the transparents into, colour plus depth.
- **Deferred:** the depth lens's "find the game's depth RT and prove it with raycasts". The in-camera depth buffer makes it unnecessary; the probe idea becomes part of the self-check.
- **Corrected:** REPO. The brief says "URP 2022", but the survey found it is Built-in. T2 therefore changes REPO as well, so it must be live-tested.
- **Corrected:** the 2022 editor is not missing (see above).

## Cases

| # | Pattern (general) | Owner's games | Today | Fix | P |
|---|---|---|---|---|---|
| 1 | Built-in: world shaders without a ShadowCaster pass are missing from `_CameraDepthTexture` | ULTRAKILL (confirmed); any unlit or retro Built-in game | pass 0 after image effects against the depth texture; forced extra opaque prepass | T2: inside the camera at `AfterForwardAlpha`, FIRST, pass 1 against the depth buffer, no prepass (since 2026-10-14 at `BeforeForwardAlpha`, FIRST, then pass 2 writes her depth: case 6) | P0 |
| 2 | Built-in: the game removes command buffers by event, and leftover colour-only targets at the same event | ULTRAKILL (re-init, HeatWaves) | attached once, appended | T2: keep-first, re-checked on a buffer-count change and once a second | P0 |
| 3 | IL2CPP + SRP: event accessors the game never uses are stripped | Schedule I (start dies); any IL2CPP SRP game | one fence stops everything; she stays on the desktop | T1: accessor rung, then field rung, per event; Start never stops on it | P0 |
| 4 | SRP after-camera path: URP's placeholder or another camera's depth texture | Schedule I after T1 (every IL2CPP URP game: no adapter) | tests against whatever is bound; the log says "walls hide her" | T3: accept only a created RT of the camera's aspect; no test plus an honest line otherwise; IL2CPP depth request by interop name | P0 |
| 5 | Any optional feature that hits a stripped member stops the plugin | any IL2CPP game (latent: MiSide and Schedule I audits are clean) | every fence is fatal | T4: `Feature.Run` degrade, `Stripped` widened, physics statics isolated | P1 |
| 6 | Transparents vs her (no depth write) | all; ULTRAKILL reported "over particles" | Urp14: transparents BEHIND her paint over her; elsewhere she covers those in front | T5: Urp14 after transparents (same policy as T2); real fix needs the shader. DONE 2026-10-14 for Built-in (in-camera mode) and Urp14: pass 2 writes her depth, drawn before the transparents (SHADER (1)). Urp17 and HDRP still draw after post-processing, over every transparent | P1 |
| 7 | Camera stacks: an overlay or HUD camera picked as the world camera; IL2CPP never sees URP overlays | ULTRAKILL HUD Camera, Schedule I OverlayCamera, MiSide CameraPersons (saved only by Camera.main) | highest depth wins | T6: stack model plus UACD by name under IL2CPP | P1 |
| 8 | Orthographic main camera | MiSide (menus pick the ortho 'Camera') | a stale picture is reprojected through an ortho matrix; nothing logged | T6: prefer a perspective world camera. T7: never draw into an ortho camera; log once | P2 |
| 9 | SRP world camera that renders into a screen texture, drawn at the context END (after the texture was shown) | none confirmed (retro URP games) | invisible unless a Screen Space-Overlay canvas shows the texture | T7: draw at that camera's own end | P2 |
| 10 | Partial `Camera.rect` (letterbox, split-screen) | none confirmed | full-target triangle | T7: `SetViewport(pixelRect)` | P2 |
| 11 | Unity ≥ 6000.3 IL2CPP: the loader crashes the game | testbed 6000.3.18 | game dies at boot | launcher gate plus Il2CppInterop overlay (follow-up) | P0, not C# |
| 12 | IL2CPP on Unity 2021.3.46+ / 2022.3.63+: base libraries 404 | none installed yet | interop fails on every launch | launcher seeds a same-line zip (follow-up) | P0, not C# |
| 13 | IL2CPP has no URP or HDRP adapters | Schedule I (no game look on her; HDRP IL2CPP gets no depth test) | context end, after post | ClassInjector adapters (follow-up) | P2 |
| 14 | Following: physics player, world scale, timeScale, player search | ULTRAKILL (3.5-unit Rigidbody player at 100 u/s; she falls 10 m behind) | teleports, runs in place, small | discovery batch (follow-up); the search cadence goes in T4 | P1/P2 |

## Ordered steps

1. **T1**: IL2CPP SRP hooks get a field rung, and `Start` never stops on them. This unblocks Schedule I.
2. **T2**: Built-in draws her inside the camera against its depth buffer, first at `AfterForwardAlpha`. This fixes ULTRAKILL's walls.
3. **T3**: honest SRP after-camera depth, and an IL2CPP URP depth request. This makes walls hide her in Schedule I.
4. **T4**: optional features degrade instead of stopping her. This is the "universal IL2CPP" robustness.
5. **T5**: Urp14 moves after the transparents, giving one placement policy with T2.
6. **T6**: camera-stack-aware default camera; IL2CPP overlay detection.
7. **T7**: SRP fallback target correctness: a texture camera is drawn at its own end, the viewport is set, nothing is drawn into an ortho camera.

Each step:
- Build all four flavours (`Release`, `-v q`) and run `dotnet test tests/Astra.Bridge.Core.Tests`.
- Run `check-game-members il2cpp` against 3164500 and 2527500: no new MISSING members, and every remaining MISSING row is used only by a `[NoInlining]` rung whose caller catches `Compat.Stripped`.
- Commit each step green. After T7, run one lean adversarial review over the whole range, then ship one version.

Owner live tests, in one session after the release:
- Schedule I (T1, T3, T6)
- ULTRAKILL (T2, T6)
- MiSide and REPO (T2 regression and look)
- Extermination Ship (T5)
- PEAK, Content Warning and Lethal Company: a quick "still works"

## Runtime self-check (design; implement after one live experiment)

The goal: notice when she is drawn but never reaches the screen, or is tested against an empty depth, and fall back. The checks measure effects, not pipelines, so they stay general.

1. **Ran** (free, every frame)
   - Her command buffer starts with `cmd.SetGlobalFloat("_AstraRan", frame % 65536)`, and `LateUpdate` reads `Shader.GetGlobalFloat`.
   - Recorded but not run for 30 frames means the buffer was removed or the event never fires. Then re-attach once, then step down the ladder.
   - This also makes the `her:` line's "drawn N" count what actually executed.
   - Live experiment first: confirm that a camera-event `SetGlobal*` is visible on the CPU with graphics jobs on and off.
2. **Reached** (a few frames per mode, about two 64×64 readbacks)
   - When: she is on screen, at least 20 % opaque, the camera is still for 3 frames, and a physics ray from the camera to her chest is clear (so no wall legitimately hides her).
   - Capture her screen rectangle at the end of frame A (drawn) and frame B (her draw skipped: one 16 ms blink, at most 3 per draw mode).
   - Use `ScreenCapture.CaptureScreenshotIntoRenderTexture` plus `AsyncGPUReadback`, both guarded by `Compat.Stripped`.
   - Verdict: `mean|A−B|` over her opaque pixels must exceed `max(0.04, 3×)` the same measure over a control ring around her rectangle. A palette or pixelation changes colours but not presence.
   - On failure, step down the ladder:
     - Built-in: in-camera → after image effects.
     - SRP: in-camera adapter → after the camera → context end on the display back buffer.
   - Log each step. If every rung fails, apply B1: release her to the desktop with one line.
3. **Occlusion sanity** (only on the depth-texture path, every 5 s)
   - Cast 9 rays from the camera through a 3×3 grid around her and read the same 9 depth texels with a 1×1 `AsyncGPUReadback` each.
   - If at least 2 of 3 hits are nearer than the texel says, the texture lacks the world: switch to the in-camera depth-buffer mode and log it.
   - Static, GPU-free hint: count the opaque shaders that have no `LightMode=ShadowCaster` pass (`FindPassTagValue`, Unity 2021.2+).

The cost is near zero per frame, and nothing runs once a mode is proven. The IL2CPP availability of the capture APIs and their timing in each pipeline need the live experiment, so this is booked as a follow-up and not as a task in this batch.

## What needs an integration (not the foundation)

- Where she stands (first-person placement was reverted on the owner's rule).
- The local player in multiplayer games where the heuristics pick another player.
- Drawing her inside portal or mirror views: ULTRAKILL's portals, through `ExtraCamera`/`AlsoDrawInto`.
- A game-specific camera, only if T6's stack model still picks wrong.
- A game's own scale where the measured player height is implausible.


## Follow-ups (open)

- SHADER:
  - (1) DONE 2026-10-14. Pass 2 `HerDepth` (ColorMask 0, ZWrite On, ZTest LEqual, the same `DeviceDepth` as pass 1, `clip(alpha - 0.5)`). Drawn right AFTER pass 1, not before: same picture, and her colour never depends on two programs computing her depth to the last bit (pass 1 tests `LEqual` against the world, then pass 2 writes). Built-in in-camera mode is at `BeforeForwardAlpha` (kept FIRST), Urp14 at `AfterRenderingSkybox`. Proven on the testbed's `-astraScene glass` (a glass pane over half of her, sparks in front of her, a dense cloud of sparks half a metre behind her) in Unity 2020.3 Built-in and 2022.3 URP 14: before, she covered the sparks and the glass in front of her; after, they lie over her and the cloud behind her is hidden where she stands; with pass 2 removed (the draw point moved alone) the cloud behind her paints over her. Urp17 and HDRP still draw after post-processing, over every transparent object (moving them in front is its own change: Urp17's render graph, HDRP's custom-pass injection point).
  - The first shape of the pass (one helper handing back colour and depth, `clip` after it) hit an FXC internal error, "argument pulled into unrelated predicate": Unity left the pass OUT of the bundle with no build error, and the whole shader was "not supported" in the game. `BuildBundles` now compiles every pass for every API of a bundle first and fails the build (mutation checked: the old shape fails it).
  - (2) DepthStamp, OPEN for Built-in. In URP 14 with the depth texture made by a COPY (no prepass: no SSAO, no depth priming), her pass is enqueued at the camera's start, so at `AfterRenderingSkybox` it runs before URP's `CopyDepth` and `CopyColor` (`ScriptableRenderer.SortStable` is stable; URP 14.0.12 source): `_CameraDepthTexture` and the opaque texture get her too (read from the source, not checked live). Built-in forward makes its depth texture in a prepass before her; stamping it means switching render targets inside the camera at `BeforeForwardAlpha`, and nothing re-binds the camera's own targets for the transparents after a command buffer (the `HeatWaves` lesson): it needs a live check in ULTRAKILL and MiSide before it ships. (3) Orthographic rays and (4) a UV scale for pass 0 under dynamic resolution: OPEN.
- OLD LINES (2026-10-14): editors 2019.4.41f2 and 2020.3.49f1 (the newest of each, 2025 security patches) at `/mnt/data/Unity/Hub/Editor`, from the release API's tarballs (the Windows module is a `.pkg`: `bsdtar` it, then its `Payload`, into `PlaybackEngines/WindowsStandaloneSupport`). Both need `libgconf-2.so.4` beside `libxml2.so.2` in `UNITY_EXTRA_LIBS`; 2020's C# compiler needs `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (both scripts set it).
  - 2020: bundles built and embedded; the plugin's picker needed no change (it takes the newest bundle not newer than the game's line). Built-in testbed (`tools/build-testbed.sh … builtin`) with BepInEx 5.4.23.5: she is drawn in-camera, ~60 draws a second. `check-game-members il2cpp` against the 2019 and 2020 players' `Managed`: the only missing members are `RenderPipelineManager.add/remove_endContextRendering` (2021.1+), on the SRP path, behind the fenced accessor rung. URP 10 / HDRP 10 games on 2020: untested (no context end: drawn after the main camera; Urp14 is not loaded before 2022).
  - 2019: BLOCKED on the licence. 2019.4 gets the Personal entitlement from the Hub's licensing client, then wants a legacy ULF and stops in batch mode ("Failed to activate/update license"). Making one (`Unity.Licensing.Client --activate-ulf`, or opening that editor from the Hub) is an action on the owner's Unity account: his call. Then `tools/build-bundles.sh` with it, and the 2019 testbed.
  - IL2CPP on 2019/2020: not built. BepInEx's Unity base libraries exist for 2019.4.41 and 2020.3.49 (HTTP 200), so interop generation has what it needs; a Linux IL2CPP testbed on those lines needs that line's IL2CPP toolchain, untried.
- SELF-CHECK (needs one live experiment first): 'Ran' marker via cmd.SetGlobalFloat read back on the CPU; 'Reached' A/B capture of her screen rectangle (CaptureScreenshotIntoRenderTexture plus AsyncGPUReadback, a 1-frame skip, at most 3 per draw mode) driving a draw-point ladder and finally the B1 release; 'Occlusion sanity' of 9 raycasts vs depth texels on the depth-texture path, plus the static count of shaders with no ShadowCaster pass. Design in plan_markdown. First confirm that the capture APIs exist under IL2CPP, when they run in each pipeline, and that a camera-event SetGlobal* is visible on the CPU.
- LAUNCHER (astra-game-launcher, Rust; not foundation C#; P0, because these stop IL2CPP games before Astra.Unity runs): (a) Read the Unity version (UnityPlayer PE FileVersion or the globalgamemanagers header) and refuse to install the doorstop on IL2CPP games at Unity 6000.3 or later, where be.788 / Il2CppInterop 1.5.3 crashes the game at boot (Il2CppInterop issues #283 and #274). Lift the gate only after the 6000.3.18 testbed reaches 'Chainloader startup complete' and draws a frame with Il2CppInterop 1.5.3-ci.1121 plus PR #286 overlaid in tools/make-runner-il2cpp-gi.sh. (b) Make a HEAD request for unity.bepinex.dev/libraries/<ver>.zip. On a 404 (2021.3.46+, 2022.3.63+) seed the newest zip of the same major.minor line into BepInEx/unity-libs and point UnityBaseLibrariesSource at it. Validate once per line in staging (audit plus a drawn frame). Do NOT add MelonLoader: it uses the same interop and has the same gaps.
- IL2CPP PIPELINE ADAPTERS via ClassInjector: a ScriptableRenderPass subclass for URP 14, enqueued on beginCameraRendering through UniversalAdditionalCameraData.scriptableRenderer, drawing pass 1 in-camera. This gives Schedule I correct walls with no depth texture, and the game's look on her. A CustomPass for HDRP IL2CPP, which today has no depth test at all. The URP 17 render graph is research.
- START PROBE for the essential chain (P1, IL2CPP): bundle loading tries LoadFromMemory, then LoadFromFile from a cache file, then LoadFromStream; picture upload tries LoadRawTextureData(IntPtr), then the array overload; crop is announced in the hello only after a proven 1x1 region CopyTexture; Built-in drawing tries AddCommandBuffer, then onPostRender subscribed by its field plus ExecuteCommandBuffer. Log one 'probe:' line. An essential with no working rung sets Compositor.Failure, so B1 never claims her.
- URP 17 (PEAK, Content Warning): draw pass 1 against res.activeDepthTexture via SetRenderAttachmentDepth(Read) when the colour and depth sizes and MSAA match. Prepass-made depth textures miss shaders without a DepthOnly pass, so walls can vanish when the player enables AO. Live A/B in PEAK and Content Warning before changing a working path.
- DISCOVERY / FOLLOW batch (general, P1-P2): (1) Default size: match the measured player height (running max held about 20 s, so slides and crouches do not shrink her), and scale FollowBrain radius, height, step, keep and teleport distances by it. Cap her speed at no less than 1.2x the player's. ULTRAKILL: a 3.5-unit player at up to 100 u/s, and she trails 10 m behind. (2) A Rigidbody player is grounded by a ray plus |velocity.y|; the vertical teleport becomes relative to the player's height and applies only after a landing. (3) Count disabled-but-rendered cameras and 4:3 / 21:9 screen textures. (4) Player pick: the camera's rig ancestor first, the tagged object normalised to its rig root. (5) timeScale 0 sets speed to 0, and send a raw 'time_scale' parameter (the meaning goes in data). (6) Camera cuts: Shot.CameraId, refuse pictures from before a cut. (7) Built-in pose at onPreRender. (8) A hysteretic B1 release when an orthographic or unusable camera persists for more than 10 s.
- TEMPORAL AA / UPSCALERS / LOOK: use nonJitteredProjectionMatrix after temporal passes; check sizes before any pass 1; detect a tonemapper and send a 'tone' hint behind a capability (engine-side); clip her by the oblique near plane in mirror and portal views; extend AlsoDrawInto to Built-in and HDRP.
- SRP with no context-end event at all (Unity before 2021.1, or both T1 rungs fail): draw after the frame's last screen camera, recorded each frame, instead of at the main camera's end, which sits under a URP stack's final blit.
- Physics-less games (after T4's PhysicsQuery isolation): place her without physics on the camera's ground plane, logged once.
- OWNER INBOX (Russian, decision card, then proceed): after T2 and T5, in Built-in games (ULTRAKILL, MiSide, REPO) the game's own image effects apply to her (palette and pixelation, Colorful FX, PPv2), and DoF or depth fog may treat her as background. The way out is Picture.BeforePostProcessing=false. In URP 2022-2023 she is now drawn after transparents. A progress card for the arc too. The workflow's coordinator should post these; this subagent did not.
- INTEGRATION-ONLY items: where she stands; the local player in multiplayer games; drawing her inside ULTRAKILL's portal and mirror views (ExtraCamera / AlsoDrawInto); a game camera override only if T6 still picks wrong; Schedule I's held-item overlay order if the T6 stack model is not enough (at the context end she is drawn over the player's hands).
- PROCESS: commit T1-T7 one by one, each green (all four builds, dotnet test, IL2CPP audit on 3164500 and 2527500). Then run one lean adversarial review over the range (findings deduped, skeptics on Sonnet), fix what it confirms, and ship one version. Pushing needs the owner's word. Driver.cs is already 827 lines: split it along a real seam if T1 or T4 push it further.
