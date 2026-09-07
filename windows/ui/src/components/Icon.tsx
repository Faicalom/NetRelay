import type { SVGProps } from 'react'

export type IconName = 'dashboard' | 'connection' | 'statistics' | 'logs' | 'settings' | 'help' |
  'bell' | 'usb' | 'wifi' | 'phone' | 'computer' | 'relay' | 'download' | 'upload' |
  'clock' | 'latency' | 'refresh' | 'search' | 'copy' | 'trash' | 'check' | 'close' | 'shield' |
  'power' | 'spinner' | 'gateway' | 'internet' | 'language' | 'tools' | 'eye' | 'eyeOff'

const paths: Record<IconName, React.ReactNode> = {
  dashboard: <><rect x="3" y="3" width="7" height="7" rx="2"/><rect x="14" y="3" width="7" height="7" rx="2"/><rect x="3" y="14" width="7" height="7" rx="2"/><rect x="14" y="14" width="7" height="7" rx="2"/></>,
  connection: <><path d="M7 7h10v10H7z"/><path d="M12 2v5M12 17v5M2 12h5M17 12h5"/></>,
  statistics: <><path d="M4 19V9M10 19V5M16 19v-7M22 19H2"/></>,
  logs: <><path d="M5 5h14M5 12h14M5 19h14"/><circle cx="2" cy="5" r=".5"/><circle cx="2" cy="12" r=".5"/><circle cx="2" cy="19" r=".5"/></>,
  settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.88l.06.06-2.83 2.83-.06-.06a1.7 1.7 0 0 0-1.88-.34 1.7 1.7 0 0 0-1.03 1.56V21h-4v-.09A1.7 1.7 0 0 0 8.95 19.4a1.7 1.7 0 0 0-1.88.34l-.06.06-2.83-2.83.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.56-1.03H3v-4h.09A1.7 1.7 0 0 0 4.6 8.95a1.7 1.7 0 0 0-.34-1.88l-.06-.06 2.83-2.83.06.06A1.7 1.7 0 0 0 9 4.6a1.7 1.7 0 0 0 1-1.56V3h4v.09A1.7 1.7 0 0 0 15.05 4.6a1.7 1.7 0 0 0 1.88-.34l.06-.06 2.83 2.83-.06.06A1.7 1.7 0 0 0 19.4 9c.13.39.5.82 1.56 1.03H21v4h-.09A1.7 1.7 0 0 0 19.4 15z"/></>,
  help: <><circle cx="12" cy="12" r="9"/><path d="M9.7 9a2.4 2.4 0 1 1 3.7 2c-.9.55-1.4 1-1.4 2"/><path d="M12 17h.01"/></>,
  bell: <><path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 7h18s-3 0-3-7"/><path d="M10 19a2 2 0 0 0 4 0"/></>,
  usb: <><path d="M12 2v14M9 5l3-3 3 3M12 10H7v4M7 14l-2-2-2 2 2 2 2-2zM12 7h5v3"/><circle cx="17" cy="11" r="1.5"/><path d="M12 16v3"/><rect x="10.5" y="19" width="3" height="3" rx=".5"/></>,
  wifi: <><path d="M3 9a14 14 0 0 1 18 0M6 13a9 9 0 0 1 12 0M9.5 17a4 4 0 0 1 5 0"/><circle cx="12" cy="20" r="1"/></>,
  phone: <><rect x="6" y="2" width="12" height="20" rx="3"/><path d="M10 5h4M11 19h2"/></>,
  computer: <><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></>,
  relay: <><circle cx="12" cy="12" r="2"/><path d="M7.8 7.8a6 6 0 0 0 0 8.4M16.2 7.8a6 6 0 0 1 0 8.4M4.5 4.5a10.6 10.6 0 0 0 0 15M19.5 4.5a10.6 10.6 0 0 1 0 15"/></>,
  download: <><path d="M12 3v13M7 11l5 5 5-5M4 21h16"/></>, upload: <><path d="M12 21V8M7 13l5-5 5 5M4 3h16"/></>,
  clock: <><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></>, latency: <><path d="M4 17a8 8 0 0 1 16 0"/><path d="M12 17l4-5"/><circle cx="12" cy="17" r="1"/></>,
  refresh: <><path d="M20 6v5h-5M4 18v-5h5"/><path d="M18.5 10A7 7 0 0 0 6 6.5L4 11M5.5 14A7 7 0 0 0 18 17.5L20 13"/></>,
  search: <><circle cx="10" cy="10" r="6"/><path d="M15 15l6 6"/></>, copy: <><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/></>,
  trash: <><path d="M4 7h16M9 7V4h6v3M7 7l1 14h8l1-14M10 11v6M14 11v6"/></>,
  check: <path d="M4 12l5 5L20 6"/>, close: <path d="M5 5l14 14M19 5L5 19"/>,
  shield: <><path d="M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6l8-4z"/><path d="M8.5 12l2.3 2.3 4.7-5"/></>,
  power: <><path d="M12 2v10"/><path d="M6.3 5.8a8 8 0 1 0 11.4 0"/></>,
  spinner: <><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 4v6h-6"/></>,
  gateway: <><rect x="3" y="6" width="18" height="12" rx="3"/><path d="M7 12h.01M11 12h6"/></>,
  internet: <><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/></>,
  language: <><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/></>,
  tools: <><path d="M14.7 6.3a4 4 0 0 0-5-5L12 3.6 9.6 6 7.3 3.7a4 4 0 0 0 5 5L20 16.4a2.5 2.5 0 1 1-3.6 3.6l-7.7-7.7"/></>,
  eye: <><path d="M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6S2 12 2 12z"/><circle cx="12" cy="12" r="2.5"/></>,
  eyeOff: <><path d="M3 3l18 18M10.6 6.2A10 10 0 0 1 12 6c6.5 0 10 6 10 6a17 17 0 0 1-3 3.7M6.1 6.1C3.4 7.8 2 12 2 12s3.5 6 10 6a10 10 0 0 0 4-.8M9.9 9.9a3 3 0 0 0 4.2 4.2"/></>,
}

export function Icon({ name, ...props }: SVGProps<SVGSVGElement> & { name: IconName }) {
  return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" {...props}>{paths[name]}</svg>
}
