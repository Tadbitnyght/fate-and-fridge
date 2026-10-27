/**
 * IP allowlist range parsing shared by the setup wizard and security settings.
 * Mirrors `ClientAddress.TryNormalizeRange`; the server stays authoritative.
 */

function parseIPv4Octets(value: string): number[] | null {
  const parts = value.split('.')
  if (parts.length !== 4) {
    return null
  }
  const octets: number[] = []
  for (const part of parts) {
    if (!/^(0|[1-9]\d{0,2})$/.test(part)) {
      return null
    }
    const octet = Number(part)
    if (octet > 255) {
      return null
    }
    octets.push(octet)
  }
  return octets
}

function parseHexGroups(value: string): number[] | null {
  if (value.length === 0) {
    return []
  }
  const groups: number[] = []
  for (const part of value.split(':')) {
    if (!/^[0-9a-fA-F]{1,4}$/.test(part)) {
      return null
    }
    groups.push(parseInt(part, 16))
  }
  return groups
}

/**
 * Expands an IPv6 address (including `::` and an embedded dotted quad) into its
 * 16 bytes, so mapped forms can be detected regardless of how they are written.
 */
function parseIPv6Bytes(value: string): number[] | null {
  if (!value.includes(':')) {
    return null
  }

  let head = value
  let tailGroups: number[] = []
  const lastColon = value.lastIndexOf(':')
  const tail = value.slice(lastColon + 1)
  if (tail.includes('.')) {
    const octets = parseIPv4Octets(tail)
    if (octets === null) {
      return null
    }
    head = value.slice(0, lastColon)
    tailGroups = [(octets[0] << 8) | octets[1], (octets[2] << 8) | octets[3]]
  }

  const doubleColon = head.indexOf('::')
  let groups: number[] | null
  if (doubleColon === -1) {
    const parsed = parseHexGroups(head)
    groups = parsed === null ? null : [...parsed, ...tailGroups]
  } else {
    const left = parseHexGroups(head.slice(0, doubleColon))
    const right = parseHexGroups(head.slice(doubleColon + 2))
    if (left === null || right === null) {
      return null
    }
    const missing = 8 - (left.length + right.length + tailGroups.length)
    if (missing < 1) {
      return null
    }
    groups = [...left, ...Array<number>(missing).fill(0), ...right, ...tailGroups]
  }

  if (groups === null || groups.length !== 8) {
    return null
  }

  const bytes: number[] = []
  for (const group of groups) {
    bytes.push((group >> 8) & 0xff, group & 0xff)
  }
  return bytes
}

function isIPv4(value: string): boolean {
  return parseIPv4Octets(value) !== null
}

function isIPv6(value: string): boolean {
  return parseIPv6Bytes(value) !== null
}

/** True for `::ffff:a.b.c.d` and long forms such as `0:0:0:0:0:ffff:1.2.3.4`. */
function isIPv4Mapped(value: string): boolean {
  const bytes = parseIPv6Bytes(value)
  if (bytes === null) {
    return false
  }
  return bytes.slice(0, 10).every((byte) => byte === 0) && bytes[10] === 0xff && bytes[11] === 0xff
}

/** Validates one bare address or CIDR range; returns a problem message or undefined. */
export function validateAllowlistRange(value: string): string | undefined {
  const slash = value.indexOf('/')
  const address = (slash === -1 ? value : value.slice(0, slash)).trim()
  const prefixText = slash === -1 ? null : value.slice(slash + 1)

  if (address.length === 0 || address.includes('%') || address.includes(' ')) {
    return 'Enter a valid IPv4 or IPv6 address or CIDR range.'
  }

  let prefix: number | null = null
  if (prefixText !== null) {
    // No leading zeros: `1.2.3.4/032` is rejected like the server.
    if (!/^(0|[1-9]\d{0,2})$/.test(prefixText)) {
      return 'Enter a valid IPv4 or IPv6 address or CIDR range.'
    }
    prefix = Number(prefixText)
  }

  if (isIPv4(address)) {
    if (prefix !== null && prefix > 32) {
      return 'An IPv4 prefix must be between 0 and 32.'
    }
    return undefined
  }

  if (isIPv6(address)) {
    if (prefix !== null && prefix > 128) {
      return 'An IPv6 prefix must be between 0 and 128.'
    }
    if (prefix !== null && prefix < 96 && isIPv4Mapped(address)) {
      return 'An IPv4-mapped IPv6 range must use a prefix of /96 or longer.'
    }
    return undefined
  }

  return 'Enter a valid IPv4 or IPv6 address or CIDR range.'
}

/** Splits the textarea into trimmed non-empty lines and validates each one. */
export function validateAllowlistText(text: string): { ranges: string[]; errors: string[] } {
  const ranges: string[] = []
  const errors: string[] = []

  text.split(/\r?\n/).forEach((raw, index) => {
    const line = raw.trim()
    if (line.length === 0) {
      return
    }
    const problem = validateAllowlistRange(line)
    if (problem) {
      errors.push(`Line ${index + 1}: ${problem}`)
    } else {
      ranges.push(line)
    }
  })

  return { ranges, errors }
}
