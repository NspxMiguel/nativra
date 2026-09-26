<div align="center">

# Nativra

**Uma central de jogos no Xbox. PC nativo, emulação e menos configuração.**

**Português (Brasil) · [English](README.md)**

![Estágio](https://img.shields.io/badge/estágio-pré--alpha_prova_de_conceito-f0a020)
![Plataforma](https://img.shields.io/badge/plataforma-Xbox_Dev_Mode-107c10)
![Licença](https://img.shields.io/badge/licença-PolyForm_Noncommercial-lightgrey)

</div>

> [!WARNING]
> **AINDA NÃO ESTÁ PRONTO PARA UTILIZAÇÃO. EM FASE DE TESTES E DESENVOLVIMENTO.**
>
> O Nativra é feito por **uma pessoa só**, que também tem outros compromissos.
> É um projeto de hobby, disponibilizado de graça, sem uma equipe por trás.
> Estou investindo meu próprio dinheiro em diversas ferramentas de IA, meu
> tempo e muito esforço para tirar essa ideia do papel — quebrando a cabeça,
> errando, testando e tentando de novo.
>
> Ver um jogo abrir pela primeira vez é uma conquista, **não significa que o
> app esteja pronto**. Não há prazo prometido, garantia de compatibilidade ou
> suporte imediato. Peço paciência e respeito por esse trabalho.
>
> — **NSPXMIGUEL**

[Primeiro marco](docs/progress/2026-09-23.md) ·
[Releases](https://github.com/NspxMiguel/nativra/releases) ·
[Compatibilidade de jogos](docs/COMPATIBILITY.md) · [Como contribuir](docs/CONTRIBUTING.md) · [Licença](LICENSE)

> [!IMPORTANT]
> **Prioridade atual: rodar jogos de PC no próprio Xbox.** Neste momento, estou
> focado em fazer o jogo abrir pelo app e ficar jogável, estável e fluido.
> Por isso, outras partes do Nativra ainda estão incompletas, parcialmente
> conectadas ou sem funcionar enquanto essa função principal recebe prioridade.
> Aparecer na interface ou nos planos não significa estar pronto para uso.

## Jogos de PC rodando no Xbox

### No próprio Xbox

![Painel do Xbox aberto sobre Seraph's Last Stand, com Nativra na lista de aplicativos](docs/progress/seraph-xbox-guide-build179.png)

Captura real do console, build 179: painel do Xbox sobre o jogo, mostrando o Nativra.

### O jogo em tela cheia

![Menu real de Seraph's Last Stand no Xbox Series X](docs/progress/seraph-menu-build174.png)

Captura real do console, build 174. O menu foi exibido; partida estável e recursos
online ainda não foram validados. Não é uma montagem nem uma imagem gerada por IA.

## Integração com a Steam

Login por QR, biblioteca própria e compartilhada, coleções, busca e download de jogos — pelo Nativra. SteamAPI e recursos sociais ainda estão incompletos.

### Detalhes de um jogo da Steam

![Detalhes de um jogo Steam no Nativra com arte panorâmica, tempo de jogo e estado de instalação](docs/progress/nativra-steam-details-build189.png)

Captura real do build 189: página da biblioteca Steam, **não é o GTA V rodando**.
O tempo de jogo vem do histórico da Steam, não de partidas no Nativra.
O GTA V não está instalado nessa captura e não foi validado como compatível.
Essa página é uma interface incompleta do Nativra, não a página original da Steam.
Por enquanto, mostra apenas arte, tempo de jogo e estado de instalação. A arte
panorâmica agora aparece sem o recorte lateral anterior; o layout ainda precisa
de ajustes, incluindo o rodapé de voltar parcialmente cortado. Conquistas, última
vez jogado e o conjunto completo de ações de gerenciamento ainda não estão implementados nela.

## Dentro do app

![Biblioteca do Nativra com capas verticais e contorno branco na seleção no Xbox Series X](docs/progress/nativra-library-build188.png)

Captura real do build 188 no console: capas verticais e contorno branco no jogo
selecionado. Aparecer na biblioteca não comprova compatibilidade; WAVESHAPER
ainda não é jogável pelo carregador atual. É uma interface pré-alpha, não um
mockup. O atalho de login Steam ainda aparece mesmo com uma sessão salva;
ele não indica corretamente o estado da conta.

### Prateleira de emuladores

![Tela de emuladores do Nativra com ações de abrir e adicionar à biblioteca](docs/progress/nativra-emulators-build179.png)

Captura real do build 179 no console: sistemas listados e atalhos para a biblioteca.
A presença de um emulador aqui não comprova compatibilidade nem configuração completa.

## A ideia

Transformar um Xbox Series X|S no Dev Mode oficial em uma central de jogos única,
feita para usar do sofá, só com o controle. Executar jogos de PC nativamente é
o foco técnico principal, **não tudo que o Nativra pretende fazer**.

A experiência desejada combina a conveniência de um SteamOS com a automação de
configuração de um EmuDeck: conectar o disco, encontrar o jogo e jogar pelo mesmo
app. São referências de experiência, não afiliação nem recursos já concluídos.

- **Jogos de PC e lojas.** Execução no próprio Xbox, sem streaming. Steam é a
  primeira integração; Epic e GOG estão planejadas.
- **Biblioteca única.** Jogos de PC, jogos de emuladores e títulos da conta ainda
  não instalados, com capas, busca, coleções, favoritos e itens ocultos.
- **Facilitador de emulação.** Instalar e configurar emuladores, controles e
  pastas; importar os arquivos do usuário e identificar o sistema. A ideia é
  “colocar os jogos e jogar”, sem configurar cada emulador à mão. Executar os
  emuladores dentro do app é uma meta, não uma substituição já pronta dos pacotes UWP.
- **Arquivos e downloads no mesmo lugar.** Reconhecer e organizar arquivos do
  usuário. A entrada por magnet, `.torrent` e Telegram deve seguir o mesmo fluxo,
  perguntando o console quando houver dúvida. Não fornecemos fontes de jogos,
  trackers, BIOS proprietárias ou chaves.
- **Mods e saves.** Central de mods para jogos de PC e emulados, com integração
  planejada ao SwitchSaveSync para levar os saves do dono entre aparelhos.
- **Tudo pelo app.** Fontes comunitárias de apps/emuladores na linha do Cydia,
  instalação e atualização, configuração inicial, escolha de armazenamento,
  múltiplas contas e relatos de compatibilidade por modelo fazem parte do plano.
  Conquistas, amigos, convites e logout da Steam também são requisitos.

O processador executa instruções x86-64 nativamente. O trabalho está no carregador
de executáveis PE e nas pontes para as funções de sistema, gráficos e entrada que
um jogo de Windows espera encontrar. A mesma arquitetura de CPU não torna todos
os jogos automaticamente compatíveis.

## Escopo e estado real

| Área | Estado atual |
| --- | --- |
| Jogos de PC nativos | Menu do Seraph exibido no Series X; partida estável ainda não validada. |
| Steam | Login QR, biblioteca e código de download existem; SteamAPI e integração social/dentro dos jogos estão incompletas. |
| Biblioteca e emuladores | Telas e atalhos na prateleira existem; importação unificada por jogo ainda está incompleta. |
| Configuração de emuladores | Catálogo, ferramentas de instalação/configuração pela CLI e arquivos de configuração existem. Nem todos foram validados, e o fluxo completo ainda não funciona dentro do app. |
| Reconhecimento de BIOS e arquivos | Módulos locais de identificação/organização e guias de extração do próprio hardware existem; falta concluir o fluxo pelo controle. |
| Torrent e Telegram | Existem fila e identificação de arquivos; os dois transportes de download ainda não foram implementados. |
| Epic/GOG, mods, sincronização de saves, fontes comunitárias, multicontas e site de compatibilidade | Planejados; não entregues como funções completas. |

Detalhes no [briefing completo](docs/DESIGN-BRIEF.md), na
[configuração de emuladores](docs/EMULATORS.md), na [entrada de arquivos](docs/INTAKE.md)
e no [tratamento de BIOS do usuário](docs/BIOS.md). Esses documentos em inglês
também registram metas e investigações anteriores; não são garantias de compatibilidade.

## O que já foi demonstrado

- **Login na Steam** no console, por QR code, nativo; a sessão sobrevive às
  atualizações do app.
- **A biblioteca inteira**: jogos próprios e da Família Steam, com as coleções,
  filtros e busca da conta. Jogos emprestados pela família baixam como os seus.
- **Download no console**, direto dos servidores da Steam, para o console ou um
  pendrive/HD externo, com progresso na tela inicial, retomada depois de
  interrupção, mudança para o drive com mais espaço quando um disco enche, e
  conferência de cada bloco contra o manifesto em arquivos que um download
  interrompido deixou.
- **Jogos rodando dos próprios arquivos**, no console ou no pendrive, por um
  carregador que mapeia, reloca e liga as DLLs do jogo na ordem de dependência,
  dá a cada thread o TLS e o `DLL_THREAD_ATTACH`, e responde as APIs do Windows
  que o console não tem (pastas do usuário, arquivos do runtime C no pendrive,
  raw input, DirectInput, o compilador HLSL).
- **Verificado**: Seraph's Last Stand (Unity) roda a 60 fps no controle e mostra
  "Jogando" para os amigos da Steam.
- **Jogável**: Hades (motor próprio da Supergiant, D3D11, SDL2, FMOD) roda a
  60 fps do pendrive, com controle e som.
- **Controle remoto do console** pelo terminal, para testes.

A tabela de cada jogo testado está em [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## O que falta

**Experimental, ainda não é uma camada de compatibilidade pronta.** Jogos que usam a API C++
clássica da Steam (LEGO Jurassic World) param na inicialização da Steam, jogos de
32 bits não são suportados, jogos Direct3D 9 não têm renderizador, e o Adobe AIR
(Brawlhalla) não acha o descritor da aplicação. Multiplayer e convites de amigos
não foram validados. Nenhuma verificação de licença ou autenticação é contornada:
os jogos rodam das licenças da própria conta logada.

## Testes de desenvolvimento

Use o Dev Mode oficial e somente jogos que você possui. A
[release pré-alpha](https://github.com/NspxMiguel/nativra/releases/tag/v0.0.1-prealpha.1)
contém o pacote, as limitações e as instruções de instalação. Faça backup dos
dados antes de desinstalar: a desinstalação remove o armazenamento do aplicativo.

```bash
bun src/xbdev.ts connect
bun src/xbdev.ts install <arquivos-do-pacote>
bun src/xbdev.ts steam games
bun src/xbdev.ts launch kiosk
```

Os builds são feitos no GitHub Actions com Windows. Os caminhos internos e a
identidade do pacote ainda usam `Kiosk` para preservar compatibilidade.

Depois da primeira instalação o app se mantém atualizado: ao abrir a tela
inicial ele consulta as releases deste repositório e instala uma versão mais
nova como atualização, que preserva os jogos baixados e o login da Steam. Os
jogos leem o controle por XInput; segure View + Menu para alternar entre o
modo controle e o modo mouse e teclado.

O armazenamento do próprio app no console é pequeno e enche primeiro. Um
pendrive ou HD externo ligado ao Xbox aparece na tela de instalação com o
espaço livre e já vem escolhido quando é o que tem mais espaço; os jogos vão
para `Nativra\games` nele e rodam de lá. Um download que enche o disco move o
que já baixou para o outro drive e continua, e um download interrompido
continua de onde parou na próxima vez que o app abre. Jogos emprestados pela
Família Steam baixam como os seus.

Nunca publique `steam.json`, tokens, QR de login, credenciais, dumps de memória
ou arquivos dos jogos em issues. Revise os logs antes de compartilhar.

## Licença

Gratuito para usar, estudar, modificar e compartilhar, **não para vender**.
Consulte [LICENSE](LICENSE) e [NOTICE.md](NOTICE.md): PolyForm Noncommercial 1.0.0.

Sem jogos, BIOS ou chaves incluídos. Sem bypass de licença. Sem afiliação com
Microsoft, Xbox ou Valve.
