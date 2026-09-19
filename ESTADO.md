# Onde o projeto está — 19/09/2026 (noite)

## Provado no console (Series X, dev mode)

1. **Login da Steam** por QR, nativo. Sessão guardada e devolvida depois de
   reinstalar o app (`xbdev sync` reenvia `steam.json`).
2. **Biblioteca completa**: 39 jogos próprios + 76 da biblioteca de família =
   100. As **coleções dele** (Favorites 65, Ocultos 15, Software, z-garapa) vêm
   da conta e aparecem na lateral. Busca no X, filtros no LB/RB, selo por jogo.
3. **Download rodando no próprio console.** O cliente Steam inteiro foi portado
   para C#: websocket do CM, logon com refresh token, chave do depot, PICS,
   KeyValues, manifesto, chunks, AES, e os três contêineres (zip, VZ=LZMA,
   VS=zstd). Medido baixando GTA V a 3% direto no Xbox.
4. **Tela de detalhe do jogo** com arte, tempo de jogo, estado e instalação que
   pergunta onde gravar (console ou pasta de desenvolvimento, com espaço livre).
5. **Carregador de PE dentro do app.** Os quatro módulos do Seraph's Last Stand
   (exe, UnityPlayer, GameAssembly, baselib) mapeiam, relocam e resolvem
   **753 imports contra o Windows real do console**. Faltam 178.
6. **Controle remoto do console**: `/ext/remoteinput` por websocket. Três bytes
   por evento (`0x01`, keycode, down/up); botões de controle são keycodes 0xC3+.
   `xbdev press a`, `xbdev press right*3`, `xbdev type texto`.

## Medições que mudam o plano

- **Não é Wine/Proton.** O Xbox já é Windows NT: 753 dos 931 imports do jogo
  resolvem para a função real do sistema. O que falta não é traduzir Win32, é
  preencher os módulos que o processo UWP não tem carregados:
  `USER32` 114, `WINMM` 23, `HID` 14, `IMM32` 8, `OPENGL32` 6, `SETUPAPI` 5,
  `VERSION` 3, `dbghelp` 2 — 178 no total. Essa lista é a fila de trabalho.
- **Chamar código do jogo derruba o processo** se o entry point do módulo não
  rodou antes; e violação de acesso nativa não é capturável em código
  gerenciado. Por isso a sondagem grava o relatório antes de tentar, e só tenta
  quando existe um arquivo marcador.
- **O app NÃO alcança o Device Portal do próprio console** (isolamento de
  loopback; testado com e sem `privateNetworkClientServer`). Então instalar
  pacote de dentro do app não sai por aí — o caminho é reempacotar os
  emuladores com protocolo próprio.
- **A internet do console é livre**, e é por isso que o download funciona lá.
- Atualizar pacote no lugar deixa o app em `0x80270300`. O ciclo confiável é
  **uninstall + install com as dependências**.
- O portal cria pasta em `/api/filesystem/apps/folder` com `newfoldername`, e
  apaga arquivo com `DELETE /api/filesystem/apps/file`.

## Ferramentas

`bun src/xbdev.ts <status|apps|install|launch|shot|sync|press|type|win32|steam>`
`steam <games|info|download|shelf>`. Console no chaveiro
(`claude-autonomous:XBDEV`). Build: push -> GitHub Actions -> release ->
`gh release download`.

## Fila (pedidos dele, em PEDIDOS.md)

1. Rodar o jogo: preencher os 178 imports, começando por user32 sobre CoreWindow.
2. Tudo abre pelo nosso app (reempacotar emuladores com protocolo).
3. Instalar/atualizar emulador de dentro do app, estilo Cydia com fontes.
4. Emulador como dado, não como pacote (a analogia do Minecraft).
5. Jogo baixado no menu do Xbox, se ele quiser.
6. Achievements, Steam Cloud, Epic/GOG, multiconta, setup inicial.
7. Site estilo ProtonDB com selo por console.
8. Minecraft Java.
