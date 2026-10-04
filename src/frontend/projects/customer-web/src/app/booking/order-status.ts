/** How an order's status is told to the customer (server statuses; never a reason for a decline). */
export function orderStatusLabel(status: string): string {
  switch (status) {
    case 'AwaitingPayment':
      return 'Waiting for your details and payment';
    case 'Pending':
      return 'Being confirmed with the airline';
    case 'Confirmed':
      return 'Confirmed';
    case 'PartiallyConfirmed':
      return 'Partly confirmed';
    case 'Failed':
      return 'Not booked: nothing was charged';
    case 'Abandoned':
      return 'Expired: nothing was charged';
    case 'Cancelled':
      return 'Cancelled';
    default:
      return status;
  }
}
