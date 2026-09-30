import { calc } from './math.js';

export function cartTotal(items, rate) {
  const net = items.reduce((sum, item) => sum + item.price * item.quantity, 0);
  return calc(net, rate);
}
