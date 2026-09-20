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

## Estado do motor agora (madrugada de 20/09)

Com as respostas de janela instaladas (handle falso, atom falso, tela
1920x1080, `GetClientRect` preenchido de verdade), o motor chega a **126
funções** e **fica vivo**, mas não avança para criar janela: o anel das
últimas chamadas mostra `EnterCriticalSection`/`LeaveCriticalSection` em laço.
Ele está girando no sistema de jobs, esperando algo.

Hipóteses na ordem em que eu testaria:
1. Ele espera a thread principal bombear mensagens. `PeekMessageW` hoje devolve
   0 sempre e `GetMessageW` devolve 1 sem preencher a MSG — isso pode travar o
   laço. Preencher uma MSG zerada e devolver 0 em `GetMessageW` é mais honesto.
2. Ele pode estar bloqueado lendo os dados do jogo: conferir se
   `CreateFileW`/`ReadFile` aparecem no traço (não apareceram ainda).
3. O `WaitForSingleObjectEx` pode estar esperando um evento que nunca vem
   porque a thread que o sinalizaria morreu em silêncio.

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

## Noite de 19-20/09 — o ciclo automatico e o que ele mediu

**`./scripts-cycle.sh "mensagem" <voltas>`** faz a volta inteira: commit, push,
espera o build do sha certo, baixa a release, desinstala, instala Kiosk + as
dependencias x64, abre uma vez (senao nao existe `LocalState` e todo push falha
**em silencio**), sincroniza, manda o `autodownload.txt`, abre, e traz
`native-probe.txt` e `native-pulse.txt`.

Coisas medidas nesta noite, todas contraintuitivas:

- **O app nao le a pasta de desenvolvimento.** `D:\DevelopmentFiles` e mais
  cinco letras: `UnauthorizedAccessException` em todas. O portal le, o app nao.
  Entao o jogo e baixado de novo pelo console a cada volta (o `autodownload.txt`
  com o appid), porque `LocalState` vai embora junto com a desinstalacao.
- **`LocalState` so existe depois da primeira execucao.** Instalar e empurrar
  arquivo falha ate o app rodar uma vez.
- **`pull` precisa da pasta**: `pull kiosk native-probe.txt LocalState`. Sem ela
  o portal procura na raiz do pacote e devolve erro.
- **Gravar por cima do relatorio o esvazia primeiro**, e o processo morre dentro
  dessa janela: o que sobra e um arquivo de zero byte. Agora grava num rascunho
  e troca (`MoveAndReplaceAsync`), entao ou o antigo esta la ou o novo.
- **`HashSet` lido enquanto outra thread insere derruba o processo.** Foi erro
  meu ao tirar o lock do contador; virou um `bool[]`.
- **Pagina de codigo selada nao aceita stub novo** — e `GetProcAddress` em tempo
  de execucao cria stub novo. Cada escrita abre e fecha a pagina.

## O que foi construido nesta noite

- **`ComProxy`**: stand-in para objeto COM. Copia a tabela de metodos, entrada
  por entrada, com thunks gerados que trocam o ponteiro do objeto, e substitui
  so as entradas que a gente responde. E o que permite mexer em DXGI sem tocar
  no jogo.
- **`GraphicsBridge`**: `CreateSwapChainForHwnd` e `CreateSwapChain` caem em
  `CreateSwapChainForCoreWindow`, com a descricao reescrita para o que o console
  aceita (modelo flip, sem MSAA, dois buffers). `SetFullscreenState` responde
  que sim. A `CoreWindow` e capturada na thread de interface.
- **`LoaderStubs`**: `LoadLibrary`/`GetProcAddress`/`GetModuleHandle`. Sem isso
  a ponte do DXGI nunca seria usada — o Unity escolhe o renderizador em tempo de
  execucao, entao nada decidido na tabela de imports encosta nele.
- **`PadBridge`**: XInput por cima de `Windows.Gaming.Input`. Os dezesseis bytes
  do `XINPUT_STATE` escritos a partir da leitura do controle do console.
- **`WindowStubs`** agora responde de verdade: fila de mensagens (MSG zerada,
  `PeekMessage` devolve 0, `GetMessage` devolve WM_NULL), `GetMonitorInfo`,
  `EnumDisplayMonitors` chamando o callback do jogo com um monitor 1920x1080.
- **Pulso**: uma thread grava `native-pulse.txt` a cada 25 ms com o total de
  chamadas, o que cada thread chamou por ultimo e ha quanto tempo. O motor morre
  em menos de 2 ms as vezes, e so isso alcanca.
