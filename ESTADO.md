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

## O motor do jogo RODA no console

Estado medido em 19/09, madrugada. O Seraph's Last Stand, baixado pelo próprio
Xbox da conta dele, tem os quatro módulos carregados pelo nosso carregador e o
**motor do Unity inicializa e roda**, multithread, dentro do nosso app.

O que o traço mostra o motor fazendo, em ordem: CRT e code pages, linha de
comando por `CommandLineToArgvW`, semente aleatória por `BCryptGenRandom`,
topologia de CPU por `GetLogicalProcessorInformationEx`, `CreateThread` para o
sistema de jobs, `TlsSetValue`, e **`RaiseException` + `RtlUnwindEx`** — ou
seja, exceções C++ sendo lançadas e desenroladas dentro de uma imagem que o
sistema operacional nem sabe que existe. 124 funções distintas alcançadas, e o
processo continua vivo.

### As cinco peças que destravaram isso, todas medidas

1. **`codeGeneration` no manifesto.** Era a causa de TODOS os crashes: sem ela
   uma página escrita nunca vira executável, e a primeira chamada a código
   gerado derruba o processo. O JitProbe tinha; o Kiosk não.
2. **TLS estático.** Índice por `TlsAlloc`, cópia do template, e o bloco escrito
   na tabela do TEB (0x58). O tamanho da tabela se **mede** com `VirtualQuery` —
   `HeapSize` falha porque a tabela não é do heap do processo.
3. **`RtlPcToFileHeader`.** A máquina de exceção pergunta a todo momento qual
   imagem é dona de um endereço, e o sistema só conhece o que ele mesmo
   carregou. Nossas imagens respondem por si.
4. **`CreateProcessW`.** O Unity sobe o crash handler como processo filho antes
   de tudo. Um app container não pode, e a chamada matava o processo. Falhar com
   acesso negado é verdade e o motor segue sem ele.
5. **Chamar `UnityMain` da DLL** em vez do entry point do `.exe`: a inicialização
   de um executável assume que ele é o processo.

### Ferramenta que tornou isso possível

Um traço de chamadas estilo `+relay` do Wine: cada import resolvido recebe um
thunk gerado em runtime que empilha os quatro registradores de argumento,
registra o nome, restaura e salta para a função real. Um anel guarda as últimas
chamadas com repetição — sem isso, a função onde o jogo morre fica escondida.

## O que falta para ver a janela

- **user32 sobre CoreWindow** (114 funções): `RegisterClass`, `CreateWindowEx`,
  `GetMessage`/`PeekMessage`/`DispatchMessage`, `DefWindowProc`, `GetClientRect`,
  raw input. O Xbox não tem HWND; tem CoreWindow.
- **Ponte de gráficos**: o jogo cria swapchain a partir de HWND; o console usa
  `CreateSwapChainForCoreWindow`. É interceptar `dxgi`/`d3d11`.
- **winmm 23, HID 14, imm32 8** para som e controle.

Cada import sem resposta já tem um stub que grava o próprio nome, então a
ordem de implementação é ditada pelo jogo, não por palpite.

## Fila (pedidos dele, em PEDIDOS.md)

1. Rodar o jogo: preencher os 178 imports, começando por user32 sobre CoreWindow.
2. Tudo abre pelo nosso app (reempacotar emuladores com protocolo).
3. Instalar/atualizar emulador de dentro do app, estilo Cydia com fontes.
4. Emulador como dado, não como pacote (a analogia do Minecraft).
5. Jogo baixado no menu do Xbox, se ele quiser.
6. Achievements, Steam Cloud, Epic/GOG, multiconta, setup inicial.
7. Site estilo ProtonDB com selo por console.
8. Minecraft Java.
