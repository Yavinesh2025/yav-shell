import { totalWithTax } from './math.js';

export function cartTotal(items, rate) {
  const net = items.reduce((sum, item) => sum + item.price * item.quantity, 0);
  return totalWithTax(net, rate);
}
