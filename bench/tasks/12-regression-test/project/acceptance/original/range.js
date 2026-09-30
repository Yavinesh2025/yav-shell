export function parseRange(text) {
  const numbers = [];
  for (const part of text.split(',')) {
    const [from, to] = part.split('-').map((piece) => Number(piece.trim()));
    if (to === undefined) {
      numbers.push(from);
      continue;
    }

    for (let number = from; number <= to; number++) {
      numbers.push(number);
    }
  }

  return numbers;
}
