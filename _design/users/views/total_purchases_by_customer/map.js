(function (doc) {
    if (doc.type == 'purchase') {
        emit(doc.customer_id, doc.amount);
    }
})