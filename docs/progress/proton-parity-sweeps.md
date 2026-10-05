# Layered parity console sweeps — 2026-10-05

These are startup/rendering observations, not gameplay certification. Tests use
installed games and the existing Steam session; no sign-in,
license workaround or per-title setting was added. No physical UWP gamepad was
listed during the first sweep. Each console lease is bounded to 14 minutes plus
cleanup, below the 15-minute limit, and releases in a finally block.

## Build 498: XInput export/event layer

CI artifact from run [37379170944](https://github.com/NspxMiguel/XboxDev/actions/runs/37379170944),
commit `10e1d11`. Four games, 150 seconds each. Raw local artifacts were archived
in `.cycle/sweep-build498/<appid>/` before the next sweep. Screenshot inspection
is separate from the counter-derived classifier.

| AppID | Game | Presented frames / reported rate | Screenshot observation | Remaining evidence |
| --- | --- | --- | --- | --- |
| 268910 | Cuphead | 1,616 / 58.1 per second | Title screen | Zero XInput reads/probes; Rewired still cannot initialize XInput; missing CM_Get_Child; PlayerPrefs save failure |
| 562260 | WAVESHAPER | 6,602 / 52.1 average | Title/menu logo; overlay ~59 presents/s | XInput reads increment, no gameplay/input interaction in this run |
| 40800 | Super Meat Boy | 2,746 / 21.7 average including startup | Intro text and character art; overlay ~57 presents/s | XInput reads increment; gameplay and complete intro not validated |
| 945360 | Among Us | 1,169 / 2.0 at pulse capture | Privacy-policy screen; later screenshot overlay ~26.5 presents/s | XInput reads increment; no consent selection or gameplay performed |

The pulse and screenshot are collected at different times. Their rates can differ;
neither is a TV frame-rate measurement. Counter threshold classification called
Among Us `starts`; the screenshot still shows rendered content. No rating was
upgraded by this sweep.

## Build 502: HID, D3D9 and heap layers

CI artifact from run [37380504790](https://github.com/NspxMiguel/XboxDev/actions/runs/37380504790),
commit `6dafde5`. Four games completed at 150 seconds each; the console lock was released.
Raw evidence is archived in `.cycle/sweep-build502/<appid>/`. Other app IDs
in that archive are older runs; the table below identifies this sweep.

The first x64 launch failed before game loading with:

> is missing delegate marshalling data. To enable delegate marshalling data, add a MarshalDelegate directive

This is a Nativra AOT packaging regression, not a newly discovered game limitation.
The `.NET Native` build succeeded and the portable unit tests passed, but runtime
marshalling metadata was absent. Commit `664934a` adds an explicit MarshalDelegate
policy for native bridge callbacks. CI then exposed ILT0027: the project's
`None` build action prevented the compiler from reading the directives at all.
Commit `e3f588c` changes the build action to `EmbeddedResource`; Xbox retesting
is required. The x86 launch path bypasses the affected x64 initialization.

| AppID | Game | Presented frames / reported rate | Screenshot / result |
| --- | --- | --- | --- |
| 268910 | Cuphead | 0 / 0 | Host initialization failed: missing delegate marshalling data |
| 562260 | WAVESHAPER | 6,605 / 52.1 average | Title/menu logo; overlay ~59 presents/s; 7,472 XInput reads |
| 40800 | Super Meat Boy | 2,869 / 22.7 average including startup | Intro text and character art; overlay ~55 presents/s; 1,299 XInput reads |
| 1919460 | Seraph's Last Stand | 0 / 0 | Same host initialization failure |

The shared x86 paths retained rendering, but build 502 regresses x64 startup
and must not be promoted. No compatibility rating was upgraded.
