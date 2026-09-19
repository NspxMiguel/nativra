# Onde o projeto está — 19/09/2026

## Provado no console (Series X, 10.0.0.43, dev mode)

1. **Login da Steam funciona nativo.** Ele escaneou o QR na TV, aprovou no
   celular, e a tela virou "Signed in as miguelgamespro". Fluxo:
   `IAuthenticationService/BeginAuthSessionViaQR` -> challenge
   `https://s.team/q/1/<client_id>` -> `PollAuthSessionStatus` devolve
   `refresh_token`/`access_token`/`account_name`. Protobuf escrito à mão em
   `uwp/Kiosk/SteamAuth.cs`. Sem senha passando pelo console.
2. **JIT funciona no dev mode** (`uwp/JitProbe`): aloca RW, escreve x64, vira
   executável, chama, retorna 42. RWX direto falha (erro 87 = W^X imposto).
   => camada de tradução é arquiteturalmente possível.
3. **14 emuladores/source ports instalados e rodando** (RetroArch 218 cores,
   Xenia, XBSX2, Dolphin, Flycast, PPSSPP, GZDoom, Raze, ScummVM, DOSBox Pure,
   OpenBOR, Ikemen, Sega Model 3, Ruffle).
4. **Modo jogo ligado**: `DefaultUWPContentTypeToGame=true` (GPU inteira, mais
   RAM, enxerga HD). Pede reboot, já reiniciado.
5. **Kiosk** (front-end) instalado, lista e abre apps por protocolo URI.

## Descobertas que não podem ser perdidas

- Console renderiza em **960x540 lógicos (escala 2x)** — medida de layout é
  metade do que parece em 1920.
- App instalado por fora **não enumera outros pacotes** (`FindPackages` =
  `0x80070005`). Por isso o Mac manda a lista (`xbdev sync` -> `apps.json` no
  `LocalState`) e o app abre por **protocolo URI**. Só 5 registram protocolo:
  retroarch, xeniacanary, dolphin, flycast, supermodel.
- Caminho do Device Portal para subpasta **precisa começar com `/`**.
- Instalação é **uma por vez** (409 = fila, esperar). `204` do estado = ocioso.
- Atualizar pacote **exige mesmo certificado** (fixo em secrets) e o app
  **fechado**; senão `0x80070005`. Pacote meio-instalado dá `0x80270300` e o
  conserto é uninstall + install com o app parado.
- `RequiresPointerMode.WhenRequested` tira o cursor (senão parece navegador).

## Fundido num app só (19/09)

O `Vapor` deixou de existir: a tela de QR e o cliente Steam moraram para dentro
do `Kiosk`, que agora é **o** app do console. A home traz "Steam" no trilho ao
lado dos emuladores; B volta, Y recarrega, X sai da conta.

Arquivos: `uwp/Kiosk/SteamAuth.cs` (protobuf + QR + renovação de token),
`SteamSession.cs` (guarda conta/tokens/steamid em `steam.json`, steamid lido do
claim `sub` do JWT — dispensa chave de API), `SteamLibrary.cs`
(`GetOwnedGames`), `SteamPage.xaml` (login e biblioteca).
Pacote `Vapor` desinstalado do console; projeto fora da solução e do workflow.

## Ordem combinada

interface usável -> login -> biblioteca -> download -> rodar jogo ->
achievements -> Steam Cloud -> Epic/GOG.

Feito até aqui: interface, login, biblioteca. **Próximo: download.**

### O que o download exige (o bloco grande)

Baixar depot não é HTTP simples: a chave de descriptografia do depot só sai
pela conexão de cliente (CM, websocket `wss://cmN.steampowered.com/cmsocket/`),
não pela Web API. Sequência: conectar no CM -> `Logon` com o `access_token` ->
`GetDepotDecryptionKey` -> `GetManifestRequestCode` -> baixar manifesto do CDN
(`IContentServerDirectoryService/GetServersForSteamPipe`) -> baixar os chunks
-> descriptografar (AES) e descomprimir (LZMA/zip). É o mesmo caminho do
DepotDownloader.

Ferramentas: `bun src/xbdev.ts <status|apps|install|launch|shot|sync|verify>`.
Console no chaveiro (`claude-autonomous:XBDEV`). Build: push -> GitHub Actions
-> release -> `gh release download`.
