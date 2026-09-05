export function when(iso: string | undefined | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { hour12: false });
}

export function ago(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60);
  if (h < 48) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}

export const num = (n: number) => n.toLocaleString();

/** Cost is an estimate; a missing price is unknown, never zero. */
export function cost(usd: number | undefined | null): string {
  return usd === undefined || usd === null ? 'no price for this model' : `$${usd.toFixed(5)}`;
}

export const short = (id: string, n = 8) => (id.length > n ? id.slice(0, n) + '…' : id);
