longshot process(es): 2
  PID   9872  GAME  bin/longshot.exe  C:\Recomp Library\longshot\Longshot\bin\longshot.exe
  PID  16992  GAME  bin/longshot.exe  C:\Recomp Library\longshot\Longshot\bin\longshot.exe
=== detected game process ===
  PID:           9872
  Executable:    C:\Recomp Library\longshot\Longshot\bin\longshot.exe
  Game folder:   C:\Recomp Library\longshot\Longshot
  Build version: v3.0.0  source: 87cb36aa
  nup_build_id:  e0f3ccfa
  profile:       Longshot
  control pipe:  longshot_control
PS C:\Users\Ty\Desktop\Coding\Switch Achievement Tracker> dotnet run --project src/SwitchAchievements.Longshot.Poc -- status
game: PID 9872  C:\Recomp Library\longshot\Longshot\bin\longshot.exe
build: version=v3.0.0 source=87cb36aa nup_build_id=e0f3ccfa
[bridge] connected to \\.\pipe\longshot_control
  frame=3722  protocol=1  fps=47.8  state=play  stalled=False
  draws=1754.5  replayed=3717  uptime=54.8s  clients=1
  map=KokiriForest_Child  version=v3.0.0 (source 87cb36aa)
  renderer=NVIDIA GeForce RTX 4060 (Vulkan, native)
  game: map=KokiriForest_Child  age=child  event=false  loading=false
        life=12/12  rupees=0  pos=(-10.3, 110.0, 3.1)
        prompt=Empty  music=BGM_KokiriForest
        flags=(none)
PS C:\Users\Ty\Desktop\Coding\Switch Achievement Tracker> dotnet run --project src/SwitchAchievements.Longshot.Poc -- watch
[bridge] connected to \\.\pipe\longshot_control
[watch] connected (pipe 'longshot_control'); subscribing oot every=60 flags=PlayerStatus.Life changes=True
[watch] subscribed. Press Ctrl+C to stop. Perform your in-game action now (enter a door, collect a rupee, take damage).
[oot #1] frame=4218 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=81.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: {}
[oot #2] frame=4277 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=81.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: {}
  >>> CHANGED FLAGS (#1): {0xe53eb6d1=472, 0xe573f564=472}
[oot #3] frame=4338 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=81.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=472 0xe573f564=472
  >>> CHANGED FLAGS (#2): {0xe53eb6d1=473, 0xe573f564=473}
[oot #4] frame=4397 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=81.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=473 0xe573f564=473
  >>> CHANGED FLAGS (#3): {0xe53eb6d1=475, 0xe573f564=475}
[oot #5] frame=4457 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=81.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=475 0xe573f564=475
  >>> CHANGED FLAGS (#4): {0xe53eb6d1=476, 0xe573f564=476}
[oot #6] frame=4518 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 110, 3.1] facing=81.9 camera=65.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=476 0xe573f564=476
  >>> CHANGED FLAGS (#5): {0xe53eb6d1=477, 0xe573f564=477}
[oot #7] frame=4577 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10, 110.4, 5.6] facing=-41.9 camera=5.8 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=477 0xe573f564=477
  >>> CHANGED FLAGS (#6): {0xe53eb6d1=478, 0xe573f564=478}
[oot #8] frame=4638 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.7, 111.6, 10.4] facing=-9.6 camera=1.2 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=478 0xe573f564=478
  >>> CHANGED FLAGS (#7): {0xe53eb6d1=480, 0xe573f564=480}
[oot #9] frame=4698 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-9.7, 111.7, 12.7] facing=-75.6 camera=-48.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=480 0xe573f564=480
  >>> CHANGED FLAGS (#8): {0xe53eb6d1=481, 0xe573f564=481}
[oot #10] frame=4758 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-10.3, 111.7, 13] facing=-13.6 camera=-13.6 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=481 0xe573f564=481
  >>> CHANGED FLAGS (#9): {0xe53eb6d1=482, 0xe573f564=482}
[oot #11] frame=4818 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-9.8, 111.7, 15.6] facing=122.6 camera=35.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=482 0xe573f564=482
  >>> CHANGED FLAGS (#10): {0x27c52681=1, 0xe53eb6d1=483, 0xe573f564=483}
[oot #12] frame=4878 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-9.9, 111.7, 14] facing=-156.6 camera=27.5 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x27c52681=1 0xe53eb6d1=483 0xe573f564=483
  >>> CHANGED FLAGS (#11): {0x27c52681=0, 0xe53eb6d1=484, 0xe573f564=484}
[oot #13] frame=4939 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-8.7, 111.8, 11.5] facing=105.9 camera=88.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x27c52681=0 0xe53eb6d1=484 0xe573f564=484
  >>> CHANGED FLAGS (#12): {0xe53eb6d1=485, 0xe573f564=485}
[oot #14] frame=4998 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-3.9, 111.5, 11.6] facing=-6.8 camera=38.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=485 0xe573f564=485
  >>> CHANGED FLAGS (#13): {0xe53eb6d1=487, 0xe573f564=487}
[oot #15] frame=5059 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-4, 111.5, 11.6] facing=-67.2 camera=-1.5 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=487 0xe573f564=487
  >>> CHANGED FLAGS (#14): {0xe53eb6d1=488, 0xe573f564=488}
[oot #16] frame=5118 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-5.6, 112.3, 14.2] facing=-0.7 camera=-5.2 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=488 0xe573f564=488
  >>> CHANGED FLAGS (#15): {0x27c52681=1, 0xe53eb6d1=489, 0xe573f564=489, 0xd852fccd=1}
[oot #17] frame=5178 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-4.6, 113.3, 18.1] facing=-20.2 camera=-83.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x27c52681=1 0xe53eb6d1=489 0xe573f564=489 0xd852fccd=1
  >>> CHANGED FLAGS (#16): {0x27c52681=0, 0xe53eb6d1=490, 0xe573f564=490}
[oot #18] frame=5239 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-6.2, 113.9, 22.3] facing=-56.7 camera=-103.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x27c52681=0 0xe53eb6d1=490 0xe573f564=490
  >>> CHANGED FLAGS (#17): {0x18da5df2=1, 0xe53eb6d1=491, 0xe573f564=491}
[oot #19] frame=5298 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-11, 114.9, 24.2] facing=-88.7 camera=-139.3 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x18da5df2=1 0xe53eb6d1=491 0xe573f564=491
  >>> CHANGED FLAGS (#18): {0x18da5df2=0, 0xe53eb6d1=492, 0xe573f564=492}
[oot #20] frame=5359 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-15.5, 116, 20.8] facing=-167.5 camera=137.1 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x18da5df2=0 0xe53eb6d1=492 0xe573f564=492
  >>> CHANGED FLAGS (#19): {0xe53eb6d1=494, 0xe573f564=494}
[oot #21] frame=5418 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-12.7, 117.4, 17.7] facing=93.5 camera=93.1 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=494 0xe573f564=494
  >>> CHANGED FLAGS (#20): {0xe53eb6d1=495, 0xe573f564=495}
[oot #22] frame=5478 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=495 0xe573f564=495
  >>> CHANGED FLAGS (#21): {0xb8b1c1af=1, 0xa9807ba4=1, 0xe53eb6d1=496, 0xe573f564=496}
[oot #23] frame=5538 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xb8b1c1af=1 0xa9807ba4=1 0xe53eb6d1=496 0xe573f564=496
  >>> CHANGED FLAGS (#22): {0xe53eb6d1=498, 0xe573f564=498}
[oot #24] frame=5599 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=498 0xe573f564=498
  >>> CHANGED FLAGS (#23): {0xe53eb6d1=499, 0xe573f564=499}
[oot #25] frame=5659 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=499 0xe573f564=499
  >>> CHANGED FLAGS (#24): {0xe53eb6d1=500, 0xe573f564=500}
[oot #26] frame=5719 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=500 0xe573f564=500
  >>> CHANGED FLAGS (#25): {0xe53eb6d1=501, 0xe573f564=501}
[oot #27] frame=5779 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=501 0xe573f564=501
  >>> CHANGED FLAGS (#26): {0xe53eb6d1=503, 0xe573f564=503}
[oot #28] frame=5838 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=503 0xe573f564=503
  >>> CHANGED FLAGS (#27): {0xe53eb6d1=504, 0xe573f564=504}
[oot #29] frame=5899 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=504 0xe573f564=504
  >>> CHANGED FLAGS (#28): {0xe53eb6d1=505, 0xe573f564=505}
[oot #30] frame=5959 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=505 0xe573f564=505
  >>> CHANGED FLAGS (#29): {0xe53eb6d1=506, 0xe573f564=506}
[oot #31] frame=6019 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=506 0xe573f564=506
  >>> CHANGED FLAGS (#30): {0xe53eb6d1=509, 0xe573f564=509}
[oot #32] frame=6079 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=509 0xe573f564=509
  >>> CHANGED FLAGS (#31): {0x31b8cfe3=1, 0x56144453=0, 0xe53eb6d1=510, 0xe573f564=510}
[oot #33] frame=6139 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-11.8, 117.5, 17.4] facing=130 camera=108.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x31b8cfe3=1 0x56144453=0 0xe53eb6d1=510 0xe573f564=510
  >>> CHANGED FLAGS (#32): {0xe53eb6d1=511, 0xe573f564=511}
[oot #34] frame=6199 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-12.3, 117.5, 17.8] facing=-51.4 camera=143.4 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=511 0xe573f564=511
  >>> CHANGED FLAGS (#33): {0x56144453=1, 0xe77ee8e9=1, 0xe53eb6d1=512, 0xe573f564=512}
[oot #35] frame=6260 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-14, 117, 16.9] facing=-122 camera=-122.3 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x56144453=1 0xe77ee8e9=1 0xe53eb6d1=512 0xe573f564=512
  >>> CHANGED FLAGS (#34): {0xe53eb6d1=514, 0xe573f564=514}
[oot #36] frame=6320 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-14.4, 116.2, 15.4] facing=148.2 camera=-145.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=514 0xe573f564=514
  >>> CHANGED FLAGS (#35): {0xe53eb6d1=515, 0xe573f564=515}
[oot #37] frame=6379 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-12.9, 111.8, 11.6] facing=-178.9 camera=158.7 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=515 0xe573f564=515
  >>> CHANGED FLAGS (#36): {0x18da5df2=1, 0xc4f23190=10, 0xe53eb6d1=516, 0xe573f564=516}
[oot #38] frame=6440 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-14.3, 110.8, 6.3] facing=167.4 camera=123.8 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x18da5df2=1 0xc4f23190=10 0xe53eb6d1=516 0xe573f564=516
  >>> CHANGED FLAGS (#37): {0xe53eb6d1=517, 0xe573f564=517}
[oot #39] frame=6499 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-14.6, 110, 0.8] facing=-167.9 camera=-157 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=517 0xe573f564=517
  >>> CHANGED FLAGS (#38): {0xe53eb6d1=519, 0xe573f564=519}
[oot #40] frame=6559 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-15.9, 109.5, -4.2] facing=160.2 camera=-159.2 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=519 0xe573f564=519
  >>> CHANGED FLAGS (#39): {0x18da5df2=0, 0xe53eb6d1=520, 0xe573f564=520}
[oot #41] frame=6620 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-13.9, 109.3, -8.9] facing=156.9 camera=-158.1 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x18da5df2=0 0xe53eb6d1=520 0xe573f564=520
  >>> CHANGED FLAGS (#40): {0xe53eb6d1=521, 0xe573f564=521}
[oot #42] frame=6680 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-12.7, 109.8, -13.4] facing=-159.5 camera=-154.2 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=521 0xe573f564=521
  >>> CHANGED FLAGS (#41): {0xe53eb6d1=522, 0xe573f564=522}
[oot #43] frame=6740 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-16.4, 109.7, -16.3] facing=-108.1 camera=-152.1 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=522 0xe573f564=522
  >>> CHANGED FLAGS (#42): {0xe53eb6d1=523, 0xe573f564=523}
[oot #44] frame=6799 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=0 pos=[-20.4, 109.7, -17.5] facing=-107.1 camera=-152.1 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=523 0xe573f564=523
  >>> CHANGED FLAGS (#43): {0xe53eb6d1=524, 0xe573f564=524}
[oot #45] frame=6859 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-21, 109.8, -18.3] facing=-62.1 camera=-153.8 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=524 0xe573f564=524
  >>> CHANGED FLAGS (#44): {0x1984801f=1, 0x18da5df2=1, 0xe53eb6d1=525, 0xe573f564=525}
[oot #46] frame=6919 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-21, 109.8, -18.3] facing=-62.1 camera=-153.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x1984801f=1 0x18da5df2=1 0xe53eb6d1=525 0xe573f564=525
  >>> CHANGED FLAGS (#45): {0xe53eb6d1=526, 0xe573f564=526}
[oot #47] frame=6980 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-21, 109.8, -18.3] facing=-62.1 camera=-153.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=526 0xe573f564=526
  >>> CHANGED FLAGS (#46): {0xe53eb6d1=527, 0xe573f564=527}
[oot #48] frame=7039 map=KokiriForest_Child age=child event=true loading=false life=12/12 rupees=0 pos=[-21, 109.8, -18.3] facing=-62.1 camera=-153.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=527 0xe573f564=527
  >>> RUPEES CHANGED: 0 -> 1
  >>> CHANGED FLAGS (#47): {0xe53eb6d1=529, 0xe573f564=529}
[oot #49] frame=7100 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=1 pos=[-21, 109.8, -18.3] facing=26.1 camera=-153.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=529 0xe573f564=529
  >>> CHANGED FLAGS (#48): {0xae1a944a=1, 0xa77921d7=1, 0xc66e58f2=1, 0xe53eb6d1=530, 0xe573f564=530}
[oot #50] frame=7159 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=1 pos=[-21, 109.8, -18.3] facing=26.1 camera=-153.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xae1a944a=1 0xa77921d7=1 0xc66e58f2=1 0xe53eb6d1=530 0xe573f564=530
  >>> CHANGED FLAGS (#49): {0xe53eb6d1=531, 0xe573f564=531}
[oot #51] frame=7220 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=1 pos=[-21, 109.8, -18.3] facing=26.1 camera=-153.9 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=531 0xe573f564=531
  >>> CHANGED FLAGS (#50): {0x18da5df2=0, 0xe53eb6d1=532, 0xe573f564=532}
[oot #52] frame=7279 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=1 pos=[-21, 109.8, -18.3] facing=26.1 camera=-153.1 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0x18da5df2=0 0xe53eb6d1=532 0xe573f564=532
  >>> CHANGED FLAGS (#51): {0xe53eb6d1=533, 0xe573f564=533}
[oot #53] frame=7340 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=1 pos=[-21, 109.8, -18.3] facing=26.1 camera=-153 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=533 0xe573f564=533
  >>> CHANGED FLAGS (#52): {0xe53eb6d1=534, 0xe573f564=534}
[oot #54] frame=7400 map=KokiriForest_Child age=child event=false loading=false life=12/12 rupees=1 pos=[-21, 109.8, -18.3] facing=26.1 camera=-153 prompt=Empty music=BGM_KokiriForest | flags: PlayerStatus.Life=12 | changed: 0xe53eb6d1=534 0xe573f564=534
  >>> CHANGED FLAGS (#53): {0xe53eb6d1=535, 0xe573f564=535}