# Quick Reference

## Project Snapshot

- The loai: 2D platform puzzle.
- Mode: single player va 2-player online co-op.
- Online: Unity Relay join code.
- Input: keyboard va mobile UI button.
- Target hien tai: PC/Editor va Android APK test.

## Play Flow

Single:

1. Menu > Single.
2. Chon dino.
3. Vao level.
4. Player duoc spawn tai `Spawn_1`.

Multiplayer:

1. Host: Menu > Multiplayer > Create Room.
2. Host copy/share join code.
3. Client: nhap join code > Join.
4. Host va client chon dino.
5. Host thay client da chon dino.
6. Host bam Play.
7. Ca hai vao level.

## Files Quan Trong

| File | Dung de lam gi |
|---|---|
| `Assets/Script/manager/MainManager.cs` | Menu flow, create/join room, chon dino, start game |
| `Assets/Script/NetworkConfig.cs` | Relay, Unity Services, NetworkManager persistence, single spawn |
| `Assets/Script/LobbyManager.cs` | Sync dino trong lobby |
| `Assets/Script/PlayerMovement.cs` | Movement, jump, animation sync, mobile API, no-friction player collider |
| `Assets/Script/MobileControlButton.cs` | Nut mobile UI |
| `Assets/Script/PlayerSkinManager.cs` | Sync skin dino |
| `Assets/Script/PlayerSpawner.cs` | Spawn player vao spawn point |
| `Assets/Script/KillZoneReload.cs` | Reload scene khi player roi vao DieZone |
| `Assets/Script/TrapAction.cs` | Action chung cho trap |
| `Assets/Script/TrapTrigger.cs` | Trigger kich hoat trap |
| `Assets/Script/TrapButton.cs` | Button trap press/toggle/hold |
| `Assets/Script/PushBlock.cs` | Khoi vat ly de player day va de nut |

## Hierarchy Checklist

Scene `Menu`:

- [ ] Co `NetworkManager`
- [ ] `NetworkManager` co `UnityTransport`
- [ ] `NetworkManager` co `NetworkConfig`
- [ ] `NetworkManager.PlayerPrefab` tro toi `Player.prefab`
- [ ] Co `LobbyManager` + `NetworkObject`
- [ ] `MainManager` gan du reference UI
- [ ] Button OnClick tro toi class `MainManager`, khong con event cu bi missing

Gameplay scene:

- [ ] Co it nhat 2 spawn point tag `Spawn`
- [ ] Single player uu tien spawn tai object ten `Spawn_1`
- [ ] Level complete nen to chuc `Doors_Complete > Door_1 / Door_2`
- [ ] Parent `Doors_Complete` co `LevelCompleteDoorGroup`
- [ ] `Door_1` va `Door_2` co `LevelComplete` cung `groupId`
- [ ] `Door_2` se bi an trong single neu `hideSecondaryDoorInSingle` bat
- [ ] Door multiplayer nen co `NetworkObject` de sync hieu ung truot/player stick qua RPC
- [ ] Co DieZone voi `KillZoneReload`
- [ ] Trap platform neu can sync thi co `NetworkObject` + `NetworkTransform`
- [ ] Push block co tag `PushBlock` hoac `ButtonWeight`, `Rigidbody2D`, `Collider2D`, `PushBlock`
- [ ] Scene nam trong `Build Settings`

Android:

- [ ] Platform switched to Android
- [ ] Orientation: `Landscape Left`
- [ ] Mobile buttons co `MobileControlButton`
- [ ] Canvas co `GraphicRaycaster`
- [ ] Scene co `EventSystem`

## Test Checklist

Single:

- [ ] Player spawn tai `Spawn_1`
- [ ] Chon dino dung mau
- [ ] Di chuyen duoc
- [ ] Nhay duoc
- [ ] Roi vao DieZone reload scene
- [ ] Trap platform move dung
- [ ] Push block day duoc va giu duoc nut hold
- [ ] Cham door thi player dinh theo door va load scene tiep theo

Multiplayer:

- [ ] Host tao duoc room code
- [ ] Client join duoc bang code
- [ ] Host/client thay dino cua nhau
- [ ] Host bam Play thi ca hai vao level
- [ ] Ca hai spawn dung vi tri
- [ ] Ca hai dung skin da chon
- [ ] Host thay client chay animation
- [ ] Client thay host chay animation
- [ ] Client nhay duoc
- [ ] Trap kich hoat dong bo
- [ ] Push block sync dung neu co `NetworkObject` + `NetworkTransform`
- [ ] DieZone reload ca team
- [ ] Ca hai player vao 2 door thi moi complete level

Android:

- [ ] Game mo ngang
- [ ] Nut trai/phai giu de di chuyen
- [ ] Tha nut thi dung
- [ ] Nut jump nhay duoc
- [ ] UI khong che man choi qua nhieu

## Loi Thuong Gap

| Loi | Can kiem tra |
|---|---|
| Join code not found | Code moi, host con chay, cung Unity Project ID |
| Client khong vao scene | Host moi duoc bam Play, scene co trong Build Settings |
| Single khong spawn player | Kiem tra `NetworkManager.PlayerPrefab` va object `Spawn_1` |
| Spawn sai/bao thieu spawn | Gameplay scene can `Spawn_1` cho single va 2 object tag `Spawn` cho multi |
| Client khong thay platform move | Trap platform can `NetworkObject` + `NetworkTransform` |
| Push block khong giu nut | Kiem tra tag co nam trong `TrapButton.extraActivatorTags` |
| Push block khong day duoc | Kiem tra `Rigidbody2D` dynamic, collider, layer collision |
| Push block bi truot sau khi tha | Bat `Stop When Not Pushed`, tang Linear Damping/friction |
| Player bi dinh canh platform | Bat `Use No Friction Material` tren `PlayerMovement` |
| Skin sai mau | Kiem tra `PlayerSkinManager` va `dinoSkins` tren prefab |
| Mobile button khong bam duoc | EventSystem, GraphicRaycaster, Raycast Target |

## Tai Lieu Noi Bo

- `MULTIPLAYER_SETUP_GUIDE.md`: tong quan project va multiplayer.
- `RELAY_SETUP_GUIDE.md`: setup Relay va fix loi join.
- `Docs/Multiplayer/Quick-Match-Workflow.md`: luong Quick Match, trang thai nut, quy tac cancel va checklist test.
- `Docs/Multiplayer/Quick-Match-Troubleshooting.md`: cac loi Matchmaker/Relay da gap va cach xu ly.
- `TRAP_SETUP_GUIDE.md`: setup trap trong hierarchy.
- `CODE_REVIEW_SUMMARY.md`: trang thai code va nhung gi da lam.
