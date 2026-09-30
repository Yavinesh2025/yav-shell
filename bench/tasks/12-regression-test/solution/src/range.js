export function parseRange(text) {
  const numbers = [];
  for (const part of text.split(',')) {
    const [first, second] = part.split('-').map((piece) => Number(piece.trim()));
    if (second === undefined) {
      numbers.push(first);
      continue;
    }

    const from = Math.min(first, second);
    const to = Math.max(first, second);
    for (let number = from; number <= to; number++) {
      numbers.push(number);
    }
  }

  return numbers;
}
