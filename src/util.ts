// Pure helpers, kept apart from the CLI so they can be tested without running it.

export function human(bytes: number): string {
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)}${units[unit]}`;
}

/** Package files the console accepts. */
export const PACKAGE_EXTENSIONS = [
  ".msixbundle",
  ".appxbundle",
  ".msix",
  ".appx",
  ".cer",
];

export function isPackageFile(name: string): boolean {
  return PACKAGE_EXTENSIONS.some((ext) => name.toLowerCase().endsWith(ext));
}

/**
 * Install order matters: the console wants the app bundle first, then the
 * frameworks it depends on, and the certificate last. Sorting the wrong way
 * makes the install fail with a dependency error that names nothing useful.
 */
export function installOrder(a: string, b: string): number {
  const rank = (file: string) => {
    const lower = file.toLowerCase();
    if (lower.endsWith(".cer")) return 3;
    if (lower.includes("vclibs") || lower.includes("ui.xaml")) return 2;
    if (lower.endsWith("bundle")) return 0;
    return 1;
  };
  return rank(a) - rank(b);
}

/**
 * The console is x64 only. Release folders ship ARM64 and x86 copies of the
 * same frameworks, and sending those makes the install fail on a mismatched
 * architecture rather than on anything meaningful.
 */
export function isForThisConsole(path: string): boolean {
  const lower = path.toLowerCase();
  if (/[\\/](arm64|arm|x86)[\\/]/.test(lower)) return false;
  if (/\.(arm64|arm|x86)\./.test(lower)) return false;
  return true;
}
