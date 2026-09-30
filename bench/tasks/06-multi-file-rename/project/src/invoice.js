import { calc } from './math.js';

export function invoiceLine(description, net, rate) {
  return `${description}: ${calc(net, rate).toFixed(2)}`;
}
