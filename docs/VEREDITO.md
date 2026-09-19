# Veredito — Xbox Series X como PC de jogos

> **Atualização 19/09/2026 — o "impossível" caiu no ponto que importa.** Medido no
> console: geração de código em runtime FUNCIONA no dev mode. O `JitProbe`
> (capability `codeGeneration`) alocou memória, escreveu x64 e executou —
> retornou 42. RWX direto falha (W^X imposto), o que é o normal e todo dynarec já
> trata. Como o console é x86-64 e jogo de PC é x86-64, **não é preciso traduzir
> instrução** para o caminho x64 — precisa da camada de ambiente Windows (PE
> loader + Win32/NT ABI), que é o que o Wine faz sem traduzir nada no x86-64.
>
> Então a divisão real do trabalho é:
> - **Launcher** (login/biblioteca/download de Steam/Epic/GOG): reimplementável em
>   código aberto, como Heroic/Legendary/nile já fazem. Engenharia conhecida.
> - **Camada tipo-Wine** dentro do que o AppContainer expõe: é o grande trabalho,
>   mas é pesquisa e engenharia, não uma porta trancada.
> - **Fora de alcance:** o `Steam.exe` fechado rodando como está, e jogo com
>   anti-cheat de kernel. O resto é caminho aberto.
>
> A seção 1 abaixo é o veredito ANTIGO (só o cliente Steam), mantido como registro.


Escrito em 19/09/2026, enquanto você dormia. Tudo que está marcado como
**medido** eu medi nesta máquina ou numa fonte primária; o que é **documentado**
veio da Microsoft ou da comunidade e ainda não passou pelo seu console, porque
ele estava desligado.

---

## 1. A pergunta que decide tudo

Você pediu Steam, Epic e as lojas principais rodando nativo, e cogitou vender o
PC por causa disso. Então a resposta tem que vir primeiro, sem rodeio:

**Não roda. Nunca vai rodar. E não é falta de esforço meu.**

São três travas do próprio console, e nenhuma tem contorno:

1. **O Xbox só executa pacote assinado.** Mesmo em Dev Mode, o que o console
   aceita instalar é um pacote MSIX/UWP que ele mesmo valida. Não existe
   caminho para rodar um `.exe` solto — não há `CreateProcess` para binário
   externo e o loader de PE do sistema não é exposto ao app.

2. **Um app UWP só carrega DLL que está dentro do próprio pacote**
   (`LoadPackagedLibrary`, documentado pela Microsoft). Todo jogo de Steam
   carrega dezenas de DLLs do disco em tempo de execução. Morre exatamente aí,
   antes de qualquer questão de desempenho.

3. **Steam e Epic são Win32 de código fechado.** Não dá para recompilar para
   UWP o que a gente não tem o código.

Fazer um "Proton do Xbox" significaria reimplementar o Wine inteiro dentro
dessas restrições — é projeto de anos, de um time, e no fim ainda esbarraria em
DRM e anti-cheat, que são feitos justamente para detectar esse tipo de camada.
Eu não vou te entregar isso como promessa.

---

## 2. Então não venda o PC

Duas perdas concretas, e as duas são suas:

- **A biblioteca Steam e Epic inteira.** Se o PC sai, ela sai junto. O Xbox não
  assume esse papel nem com esforço.
- **A única máquina Linux de build que você tem.** É a mesma que ia compilar o
  GrapheneOS pro vayu (ver `graphene-vayu-port`). Tem RTX 3050 e WSL2 dentro.

A decisão é sua, mas ela não tem volta barata: recomprar um PC equivalente custa
muito mais do que você vai conseguir vendendo esse.

---

## 3. Mas o Series X É mais forte que o seu PC — para o que ele roda

| | Xbox Series X | Seu PC |
| --- | --- | --- |
| GPU | RDNA2, **12,15 TFLOPS** | RTX 3050, **~9,1 TFLOPS** |
| Memória | **16GB GDDR6** unificada (560 GB/s) | 8GB GDDR6 na GPU + 16GB DDR4-3200 |
| CPU | Zen2 **8c/16t** @3,6 GHz | Ryzen 5 4600G, Zen2 **6c/12t** @3,7 GHz |

Em jogo de Xbox e Game Pass, o console ganha com folga. Em jogo de Steam, o
console é zero e o PC ganha por existir. As duas coisas são verdade ao mesmo
tempo, e é isso que torna a decisão de vender ruim.

---

## 4. O que EU consigo entregar, e é bastante

O Dev Mode transforma o console numa máquina de jogo nativa de verdade — tudo
rodando no próprio Xbox, com HD externo, sem PC e sem streaming:

**Jogos de PC, nativos no console**

| | Roda |
| --- | --- |
| GZDoom | Doom, Doom II, Heretic, Hexen, Strife e todo mod (Brutal Doom, Sigil, MyHouse.wad) |
| Raze | Duke Nukem 3D, Blood, Shadow Warrior, Redneck Rampage, Powerslave |
| DOSBox Pure | o catálogo DOS inteiro: Warcraft, Command & Conquer, X-COM, Dune II, Tyrian |
| ScummVM | Monkey Island, Day of the Tentacle, Grim Fandango, Broken Sword, Sam & Max |
| OpenBOR / Ikemen GO | engines de beat 'em up e de luta, com centenas de jogos livres |

**Emulação**

PS2 (XBSX2), Xbox 360 (Xenia Canary), GameCube e Wii (Dolphin), Dreamcast
(Flycast), PSP (PPSSPP), Sega Model 3, e ~200 sistemas pelo RetroArch.

São milhares de jogos, muitos deles os mesmos que você compraria na Steam — só
que rodando de verdade no console, em 4K, com o controle do Xbox.

### E tem uma ponte com a sua Steam que é real

Eu abri o pacote do RetroArch que baixei (é um zip) e contei: **218 cores**.
Entre eles, engines de jogos de PC que foram reescritos em código aberto:

| Core | O jogo de PC que ele roda |
| --- | --- |
| `boom3` | **Doom 3** |
| `vitaquake2`, `vitaquake3` | **Quake II** e **Quake III** |
| `tyrquake`, `prboom` | **Quake** e **Doom / Doom II** |
| `openlara` | **Tomb Raider** |
| `ecwolf` | **Wolfenstein 3D** |
| `dosbox_pure` | o catálogo **DOS** inteiro |
| `scummvm` | as aventuras da LucasArts e Sierra |
| `nxengine`, `reminiscence`, `easyrpg` | Cave Story, Flashback, RPG Maker |

O que isso significa na prática: você **compra o jogo na Steam** (ou já tem),
copia os arquivos de dados dele para o HD, e o engine roda **nativo no console**.
Sem PC ligado, sem streaming. Não é o cliente Steam — é o seu jogo rodando.

---

## 5. Dual boot: já vem de fábrica, e é seguro

O que você pediu — não matar o sistema original — é exatamente como o Dev Mode
funciona:

- o Dev Mode vive **numa partição separada**;
- o sistema retail, seus jogos e seus saves **não são tocados**;
- a troca é um item de menu: `Leave Developer Mode` reinicia no retail, e o app
  de ativação traz de volta;
- nada aqui é destrutivo nem irreversível, e não é jailbreak — é recurso
  oficial da Microsoft, que a sua conta de desenvolvedor de 2021 já paga.

A única consequência real: **em Dev Mode você não joga os jogos retail**. São
dois mundos, e você escolhe em qual entrar ao ligar. Game Pass e disco de um
lado; nosso sistema do outro.

---

## 6. Recursos: o detalhe que quase todo guia erra

**Documentado**, e é a diferença entre funcionar e não funcionar:

| | RAM | CPU | GPU | Vê o HD externo? |
| --- | --- | --- | --- | --- |
| App | 1 GB | 2–4 núcleos compartilhados | 45% | **não** |
| **Game** | **5 GB** | 4 exclusivos + 2 compartilhados | **100%** | **sim** |

Todo app instalado tem que ser trocado de **App** para **Game** no Device
Portal. Sem isso o emulador fica lento E cego para o HD — é a causa nº 1 de
"instalei e não funciona" nos fóruns. O `xbdev gamemode` faz isso.

---

## 7. O HD

Formate em **NTFS** (exFAT não é lido nesse caminho). O drive aparece como `E:`
dentro do RetroArch. Estrutura que eu vou deixar pronta:

```
E:\Games\<sistema>\    jogos e ROMs
E:\BIOS\               BIOS
E:\Saves\  E:\States\  saves e save states
```

---

## 8. Estado do projeto

**Pronto aqui:**

- `xbdev` — CLI que acha o console na rede, instala pacote com as dependências
  na ordem certa, troca para modo jogo, tira screenshot e lista o que está
  instalado.
- Catálogo de 14 pacotes nativos, todas as URLs validadas (16 de 16 respondendo).
- `Kiosk` — front-end nativo em UWP: lista tudo que está instalado no console e
  abre com o controle, numa tela só. É o mais perto de um SteamOS que o console
  permite.
- Pipeline de compilação UWP na nuvem, porque nenhuma máquina aqui tem Windows.

**Falta você (mandei por e-mail):** ativar o Dev Mode, ligar o Device Portal, me
dar o acesso pelo chaveiro e plugar o HD. A partir daí é um comando: `xbdev kit`.
