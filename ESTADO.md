# Onde o projeto está — 19/09/2026

## Provado no console (Series X, 10.0.0.43, dev mode)

1. **Login da Steam funciona nativo.** Ele escaneou o QR na TV, aprovou no
   celular, e a tela virou "Signed in as miguelgamespro". Fluxo:
   `IAuthenticationService/BeginAuthSessionViaQR` -> challenge
   `https://s.team/q/1/<client_id>` -> `PollAuthSessionStatus` devolve
   `refresh_token`/`access_token`/`account_name`. Protobuf escrito à mão em
   `uwp/Vapor/SteamAuth.cs`. Sem senha passando pelo console.
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

## Próximo bloco: FUNDIR num app só

Pedido dele: Kiosk + Vapor = **um app**, central de jogos plug-and-play.
Ordem combinada: interface usável -> login -> biblioteca -> download -> rodar
jogo -> achievements -> Steam Cloud -> Epic/GOG.

Imediato:
1. Mover a tela de QR/Steam do `Vapor` para dentro do `Kiosk` como seção.
   Aposentar o pacote `Vapor` (está travado meio-instalado no console).
2. Biblioteca: `IPlayerService/GetOwnedGames` com o `access_token`. O
   `refresh_token` do login dele está em `LocalState/session.txt` do Vapor.
3. Instalar emulador de dentro do app (plug and play) e config pronta de
   controle/BIOS, sem o usuário configurar nada.

Ferramentas: `bun src/xbdev.ts <status|apps|install|launch|shot|sync|verify>`.
Console no chaveiro (`claude-autonomous:XBDEV`). Build: push -> GitHub Actions
-> release -> `gh release download`.
