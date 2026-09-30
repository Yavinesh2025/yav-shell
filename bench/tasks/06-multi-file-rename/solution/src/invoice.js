import { totalWithTax } from './math.js';

export function invoiceLine(description, net, rate) {
  return `${description}: ${totalWithTax(net, rate).toFixed(2)}`;
}
