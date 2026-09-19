// Screen text lives here, never inline in the code. Portuguese and English from
// the first screen; XBDEV_LANG overrides the system language.

type Dict = Record<string, string>;

const pt: Dict = {
  "cli.usage": "uso",
  "cli.commands": "comandos",
  "cmd.find": "procura o console na rede",
  "cmd.connect": "guarda o endereco e a senha do Device Portal",
  "cmd.status": "mostra o estado do console",
  "cmd.apps": "lista o que esta instalado",
  "cmd.catalog": "lista o catalogo de apps nativos",
  "cmd.get": "baixa pacotes do catalogo",
  "cmd.install": "instala arquivos de pacote no console",
  "cmd.kit": "baixa e instala TUDO, e liga o modo jogo",
  "cmd.launch": "abre um app no console",
  "cmd.stop": "fecha um app no console",
  "cmd.shot": "tira uma foto da tela do console",
  "cmd.settings": "le ou muda um ajuste de desenvolvedor",
  "cmd.gamemode": "faz os apps rodarem como JOGO (GPU inteira, mais RAM, ve o HD)",
  "cmd.push": "manda um arquivo para a pasta de um app",
  "cmd.pull": "traz um arquivo da pasta de um app",
  "cmd.ls": "lista a pasta de um app no console",
  "cmd.setupRetroarch": "configura o RetroArch apontando para o HD",
  "cmd.verify": "confere os pacotes baixados antes de instalar",
  "verify.missing": "nao baixado",
  "verify.unreadable": "pacote ilegivel (download truncado?)",
  "verify.summary": "{checked} pacotes conferidos, {bad} com problema",
  "find.scanning": "procurando o console em {net} ...",
  "find.none":
    "nenhum console respondeu. Ligue o Xbox, entre no Dev Mode e ligue o Device Portal.",
  "find.found": "console encontrado em {host}",
  "connect.saved": "guardado no chaveiro: {host}",
  "connect.missing": "informe o endereco: xbdev connect <ip>",
  "status.offline": "console nao respondeu em {host}",
  "status.name": "nome",
  "status.os": "sistema",
  "status.type": "modelo",
  "status.apps": "apps instalados",
  "apps.none": "nada instalado ainda",
  "catalog.header": "catalogo — tudo roda NATIVO no console",
  "catalog.emulator": "emulador",
  "catalog.pcgame": "jogo de PC",
  "get.downloading": "baixando {name} ...",
  "get.cached": "{name} ja baixado",
  "get.done": "{name} pronto ({size})",
  "get.failed": "{name} falhou: {error}",
  "install.sending": "enviando {name} para o console ...",
  "install.waiting": "instalando no console ...",
  "install.done": "{name} instalado",
  "install.failed": "{name} falhou: {error}",
  "kit.start": "montando o sistema no console",
  "kit.step": "[{n}/{total}] {name}",
  "kit.done": "pronto. {ok} instalados, {fail} falharam.",
  "gamemode.looking": "procurando o ajuste de modo jogo ...",
  "gamemode.set": "modo jogo ligado ({name})",
  "gamemode.manual":
    "nao achei o ajuste automatico. No Device Portal, em cada app, troque de App para Game.",
  "shot.saved": "foto salva em {file}",
  "need.connect": "console nao configurado. Rode: xbdev connect <ip>",
  "err.generic": "erro: {error}",
};

const en: Dict = {
  "cli.usage": "usage",
  "cli.commands": "commands",
  "cmd.find": "find the console on the network",
  "cmd.connect": "store the Device Portal address and password",
  "cmd.status": "show console state",
  "cmd.apps": "list what is installed",
  "cmd.catalog": "list the native app catalogue",
  "cmd.get": "download packages from the catalogue",
  "cmd.install": "install package files onto the console",
  "cmd.kit": "download and install EVERYTHING, then turn on game mode",
  "cmd.launch": "launch an app on the console",
  "cmd.stop": "close an app on the console",
  "cmd.shot": "grab a screenshot from the console",
  "cmd.settings": "read or change a developer setting",
  "cmd.gamemode": "run apps as GAMES (full GPU, more RAM, sees the hard drive)",
  "cmd.push": "send a file into an app's folder",
  "cmd.pull": "fetch a file from an app's folder",
  "cmd.ls": "list an app's folder on the console",
  "cmd.setupRetroarch": "configure RetroArch to use the hard drive",
  "cmd.verify": "check the downloaded packages before installing",
  "verify.missing": "not downloaded",
  "verify.unreadable": "unreadable package (truncated download?)",
  "verify.summary": "{checked} packages checked, {bad} with problems",
  "find.scanning": "scanning {net} for the console ...",
  "find.none":
    "no console answered. Turn the Xbox on, enter Dev Mode and enable Device Portal.",
  "find.found": "console found at {host}",
  "connect.saved": "saved to the keychain: {host}",
  "connect.missing": "give an address: xbdev connect <ip>",
  "status.offline": "console did not answer at {host}",
  "status.name": "name",
  "status.os": "os",
  "status.type": "model",
  "status.apps": "installed apps",
  "apps.none": "nothing installed yet",
  "catalog.header": "catalogue — everything runs NATIVELY on the console",
  "catalog.emulator": "emulator",
  "catalog.pcgame": "PC game",
  "get.downloading": "downloading {name} ...",
  "get.cached": "{name} already downloaded",
  "get.done": "{name} ready ({size})",
  "get.failed": "{name} failed: {error}",
  "install.sending": "sending {name} to the console ...",
  "install.waiting": "installing on the console ...",
  "install.done": "{name} installed",
  "install.failed": "{name} failed: {error}",
  "kit.start": "building the system on the console",
  "kit.step": "[{n}/{total}] {name}",
  "kit.done": "done. {ok} installed, {fail} failed.",
  "gamemode.looking": "looking for the game mode setting ...",
  "gamemode.set": "game mode on ({name})",
  "gamemode.manual":
    "no automatic setting found. In Device Portal, switch each app from App to Game.",
  "shot.saved": "screenshot saved to {file}",
  "need.connect": "console not configured. Run: xbdev connect <ip>",
  "err.generic": "error: {error}",
};

function pickLanguage(): Dict {
  const forced = process.env.XBDEV_LANG?.toLowerCase();
  if (forced?.startsWith("pt")) return pt;
  if (forced?.startsWith("en")) return en;
  const system =
    process.env.LANG ?? process.env.LC_ALL ?? process.env.LANGUAGE ?? "";
  return system.toLowerCase().startsWith("pt") ? pt : en;
}

const dict = pickLanguage();

export function t(key: string, vars: Record<string, string | number> = {}): string {
  const template = dict[key] ?? en[key] ?? key;
  return template.replace(/\{(\w+)\}/g, (_, name) =>
    String(vars[name] ?? `{${name}}`),
  );
}
